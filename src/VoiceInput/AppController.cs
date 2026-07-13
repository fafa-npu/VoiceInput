using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VoiceInput.Models;
using VoiceInput.Services;
using VoiceInput.Views;
using WinForms = System.Windows.Forms;

namespace VoiceInput;

/// <summary>
/// Wires together the keyboard hook, audio capture, speech engine, overlay, LLM refiner, and
/// text injector, and hosts the system-tray menu. One instance owns the whole app lifecycle.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SettingsStore _store = new();
    private AppSettings _settings;

    private readonly KeyboardHook _hook;
    private readonly AudioCapture _audio = new();
    private readonly TextInjector _injector = new();
    private readonly LlmRefiner _refiner = new();
    private readonly ContextReader _contextReader = new();
    private readonly CorrectionTracker _corrections = new();
    private readonly UpdateService _updater = new();
    private string? _availableUpdateTag;
    private string? _availableAssetUrl;

    private OverlayWindow? _overlay;
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;

    private ISpeechEngine? _engine;
    private readonly StringBuilder _finals = new();
    private string _partial = string.Empty;
    private volatile float _level;
    private bool _dictating;
    private volatile bool _paused;   // when true, the PTT key is ignored (listening paused, app stays up)
    private float _sessionPeak;      // loudest mic level seen this dictation (0 ⇒ no audio captured)
    private readonly DictationSession _session = new();
    private TextInjector.Target? _inputTarget;
    private SpeechFault? _speechFault;
    private string? _pendingText;
    private Action<string>? _partialHandler;
    private Action<string>? _finalHandler;
    private Action<SpeechFault>? _faultHandler;
    // Serializes the start/stop/cancel lifecycle so sessions can never overlap
    // (Windows dictation forbids overlapping audio-engine sessions).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _finalsLock = new();   // guards _finals/_partial across engine + UI threads
    private const int StartTimeoutMs = 8000;
    private const int FirstFrameTimeoutMs = 3000;   // max wait for a cold mic to start delivering audio

    public AppController()
    {
        _ui = Application.Current.Dispatcher;
        _settings = _store.Load();

        _hook = new KeyboardHook(_settings.PttKey);
        _hook.Engaged += () => _ui.BeginInvoke(() =>
        {
            if (_session.State is DictationSessionState.Transcribing or DictationSessionState.Refining)
            {
                Notify("Still processing", "Wait for the current dictation to finish before starting another.");
                return;
            }
            _ = StartDictationAsync();
        });
        _hook.Released += () => _ui.BeginInvoke(() => _ = StopDictationAsync());
        _hook.Cancelled += () => _ui.BeginInvoke(() => _ = CancelDictationAsync());
        _hook.Submitted += () => _ui.BeginInvoke(() => _ = _corrections.CaptureAsync(_contextReader, _injector));

        _audio.LevelChanged += lvl => { _level = lvl; if (lvl > _sessionPeak) _sessionPeak = lvl; };
    }

    public void Start()
    {
        bool firstRun = !_store.Exists;   // capture before anything writes the settings file

        _overlay = new OverlayWindow { LevelSource = () => _level };
        BuildTray();
        _hook.Install();
        Log.Write($"=== VoiceInput v{UpdateService.CurrentVersion} started. ptt={_settings.PttKey}, engine={_settings.Engine}, lang={_settings.Language}, llm={_settings.LlmEnabled} ===");
        Log.Write("Windows dictation languages available: " + WindowsSpeechEngine.SupportedTopicLanguageTags());

        if (firstRun) ShowFirstRunOnboarding();

        _ = CheckForUpdatesAsync(silent: true);   // notify if a newer release exists; never auto-applies
        _ = PrewarmEntraAsync();                  // sign in once up front so dictation never triggers a focus-stealing popup
    }

    /// <summary>
    /// First launch on a machine: the app is tray-only, so a new user sees "nothing happen" after
    /// double-clicking the exe. Make it obvious it's running, explain the push-to-talk key, and open
    /// Settings so they can pick a speech engine right away. Persist defaults so this shows only once.
    /// </summary>
    private void ShowFirstRunOnboarding()
    {
        Log.Write("First run — showing onboarding.");
        Persist();   // write settings.json now so onboarding doesn't reappear on every launch

        Notify("VoiceInput is running",
            $"It lives in the system tray (blue mic, bottom-right) — there's no main window. " +
            $"Hold {PttDisplay(_settings.PttKey)} to talk, release to type. VoiceInput observes the global PTT key " +
            "but does not save ordinary keystrokes. Windows recognition may use Microsoft's online speech service; " +
            "Azure/Foundry send audio to your configured cloud. Optional UIA context sends visible app text to your LLM, " +
            "and edit learning stores encrypted correction samples locally. All optional privacy features start off.");

        // Give the tray balloon a moment to appear before the window steals attention.
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenSettings));
    }

    /// <summary>
    /// If the selected engine uses Microsoft Entra auth, sign in at startup so the browser popup
    /// happens now (not mid-dictation, where it would steal focus from the target window).
    /// </summary>
    private async Task PrewarmEntraAsync()
    {
        bool entra =
            (_settings.Engine == SpeechEngineKind.GptTranscribe && _settings.TranscribeAuthMode == AzureAuthMode.EntraId && !string.IsNullOrWhiteSpace(_settings.TranscribeEndpoint)) ||
            (_settings.Engine == SpeechEngineKind.Azure && _settings.AzureAuthMode == AzureAuthMode.EntraId && !string.IsNullOrWhiteSpace(_settings.AzureEndpoint));
        if (!entra) return;

        string tenant = _settings.Engine == SpeechEngineKind.GptTranscribe ? _settings.TranscribeTenantId : _settings.AzureTenantId;
        try
        {
            await EntraCredentialFactory.PrewarmAsync(tenant, EntraCredentialFactory.CognitiveServicesScope);
            Log.Write("Entra sign-in ready.");
        }
        catch (Exception ex)
        {
            Log.Error("Entra pre-warm", ex);
            Notify("Azure sign-in needed", "Couldn't sign in to Azure for transcription. Open Settings to retry.");
        }
    }

    // ---------------- Dictation flow ----------------

    private async Task StartDictationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_dictating || _paused) return;
            var session = _session.Begin();
            long generation = session.Generation;
            CancellationToken ct = session.Token;
            DisposeEngine();           // safety: clear any engine a prior session failed to tear down
            _dictating = true;
            _inputTarget = _injector.CaptureTarget();
            _speechFault = null;
            ClearTranscript();
            _level = 0;
            _sessionPeak = 0f;

            try
            {
                // Fragile setup is inside the try so any failure (e.g. no microphone) routes through
                // AbortInternalAsync and resets _dictating, instead of permanently wedging the app.
                _overlay!.ShowListening(Preparing());

                _engine = CreateEngine(out bool azureFellBack);

                Log.Write($"PTT engaged -> start dictation (engine={_engine.GetType().Name}, lang={_settings.Language}, azureFellBack={azureFellBack})");

                _partialHandler = text => { if (_session.IsCurrent(generation)) OnPartial(text); };
                _finalHandler = text => { if (_session.IsCurrent(generation)) OnFinal(text); };
                _faultHandler = fault => { if (_session.IsCurrent(generation)) OnSpeechFault(fault); };
                _engine.Partial += _partialHandler;
                _engine.Final += _finalHandler;
                _engine.Fault += _faultHandler;
                if (_engine is OpenAiTranscribeEngine transcribe)
                    transcribe.AuthExpired += OnTranscribeAuthExpired;

                // Start the engine first so the audio feed never arrives before it's ready.
                // Bound StartAsync so a hung SDK call can't hold the gate forever (real errors still propagate).
                var startTask = _engine.StartAsync(_settings.Language);
                if (await Task.WhenAny(startTask, Task.Delay(StartTimeoutMs, ct)) != startTask)
                {
                    ct.ThrowIfCancellationRequested();
                    _engine.Cancel();
                    _ = startTask.ContinueWith(t => Log.Error("Late engine start",
                            t.Exception ?? new Exception("Speech engine start failed without exception details.")),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    throw new TimeoutException("Speech engine start timed out.");
                }
                await startTask;
                ct.ThrowIfCancellationRequested();
                if (!_session.IsCurrent(generation) || _paused)
                    throw new OperationCanceledException(ct);

                if (_engine.NeedsAudioFeed)
                    _audio.PcmChunkAvailable += OnPcm;

                // Open (or reuse a warm) mic, then only cue the user to speak once it's actually
                // delivering audio — so a cold-start device doesn't clip the first words.
                _audio.BeginSession();
                bool live = await Task.WhenAny(_audio.FirstFrame, Task.Delay(FirstFrameTimeoutMs)) == _audio.FirstFrame;
                ct.ThrowIfCancellationRequested();
                _session.MoveTo(DictationSessionState.Listening);
                _overlay!.SetText(Placeholder());
                Log.Write(live ? "Engine started, mic live — listening…" : "Engine started, mic warm-up timed out — listening anyway…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _session.MoveTo(DictationSessionState.Cancelled);
                await AbortInternalAsync();
            }
            catch (SpeechLanguageUnavailableException ex)
            {
                Log.Error("StartAsync (language unavailable)", ex);
                Notify("Language not installed",
                    $"'{ex.Language}' dictation isn't available. Add the speech language in Settings → Time & language → Speech.");
                OpenUri("ms-settings:speech");
                await AbortInternalAsync();
            }
            catch (Exception ex) when (IsPrivacyPolicyError(ex))
            {
                Log.Error("StartAsync (privacy policy)", ex);
                Notify("Turn on Online speech recognition",
                    "Windows dictation needs Settings → Privacy & security → Speech → Online speech recognition. Or switch Engine to Azure (no toggle needed).");
                OpenUri("ms-settings:privacy-speech");
                await AbortInternalAsync();
            }
            catch (Exception ex)
            {
                Log.Error("StartDictation", ex);
                Notify("Recognition failed", ex.Message);
                await AbortInternalAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopDictationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_dictating) return;
            _dictating = false;
            _session.MoveTo(DictationSessionState.Transcribing);
            CancellationToken ct = _session.Token;

            // Batch engines (e.g. gpt-4o-transcribe) do the work on stop; show progress instead of "listening".
            if (_engine is { HasInterimResults: false })
                _overlay!.SetStatus("Transcribing…");

            await TeardownEngineAsync();
            if (ct.IsCancellationRequested)
            {
                _session.MoveTo(DictationSessionState.Cancelled);
                _overlay!.HideAnimated();
                return;
            }

            string text = ComposeTranscript();
            Log.Write(_settings.DiagnosticLogging
                ? $"PTT released -> transcript ({text.Length} chars): \"{Trim(text)}\""
                : $"PTT released -> transcript: {text.Length} chars");

            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Write($"Empty transcript — nothing recognized (peak level {_sessionPeak:F3}).");
                if (_speechFault is not null)
                    Notify("Transcription failed", _speechFault.UserMessage);
                else if (_sessionPeak < 0.02f)
                    Notify("No microphone audio",
                        "VoiceInput didn't capture any sound. If you're in a Teams/other call, it may be holding the mic — you both share one redirected microphone.");
                _overlay!.HideAnimated();
                return;
            }

            string raw = text;   // raw STT, before LLM refine — recorded for correction learning
            var target = _inputTarget;
            if (target is null || !_injector.IsCurrentTarget(target))
            {
                PreserveText(text, "The focused window or input control changed while VoiceInput was processing.");
                _overlay!.HideAnimated();
                return;
            }
            if (_settings.LlmEnabled && !string.IsNullOrWhiteSpace(_settings.LlmBaseUrl))
            {
                _session.MoveTo(DictationSessionState.Refining);
                _overlay!.SetStatus("Refining…");
                string? context = _settings.UseContext ? await _contextReader.TryReadAsync() : null;
                string refined = await _refiner.RefineAsync(text, _settings, context, _session.Token);
                if (ct.IsCancellationRequested)
                {
                    PreserveText(text, "A newer dictation cancelled refinement.");
                    _overlay!.HideAnimated();
                    return;
                }
                Log.Write(_settings.DiagnosticLogging
                    ? $"LLM refine (ctx {context?.Length ?? 0}): \"{Trim(text)}\" -> \"{Trim(refined)}\""
                    : $"LLM refine: {text.Length} -> {refined.Length} chars (ctx {context?.Length ?? 0})");
                text = refined;
            }

            _overlay!.HideAnimated();
            _session.MoveTo(DictationSessionState.Injecting);
            var result = await _injector.InjectAsync(text, target);
            if (!result.Success)
            {
                HandleInjectionFailure(text, result);
                _session.MoveTo(DictationSessionState.Failed);
                return;
            }
            Log.Write("Injected into focused control.");
            if (_settings.LearnFromEdits) _corrections.Arm(raw, text, target);
            _session.MoveTo(DictationSessionState.Idle);
        }
        finally
        {
            _session.CompleteProcessing();
            _gate.Release();
        }
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private async Task CancelDictationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_dictating) return;
            Log.Write("Dictation cancelled (chord detected).");
            await AbortInternalAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tears down the current session. Caller must already hold <see cref="_gate"/>.</summary>
    private async Task AbortInternalAsync()
    {
        _dictating = false;
        _session.MoveTo(DictationSessionState.Cancelled);
        _engine?.Cancel();   // discard: skip any network transcription for an aborted/cancelled session
        await TeardownEngineAsync();
        ClearTranscript();
        _overlay?.HideAnimated();
    }

    /// <summary>
    /// Stops audio + engine, bounding the engine stop with a timeout so a hung
    /// ContinuousRecognitionSession.StopAsync can't wedge the lifecycle, then disposes the engine.
    /// </summary>
    private async Task TeardownEngineAsync()
    {
        // Unsubscribe the PCM feed before stopping audio so no in-flight callback can Feed a closing engine.
        if (_engine is not null && _engine.NeedsAudioFeed) _audio.PcmChunkAvailable -= OnPcm;
        _audio.EndSession();
        if (_engine is not null)
        {
            bool stopped = await TryAwait(_engine.StopAsync(), _engine.StopTimeoutMs);
            if (!stopped) Log.Write("WARN engine.StopAsync timed out; forcing dispose.");
        }
        DisposeEngine();
    }

    private static async Task<bool> TryAwait(Task task, int timeoutMs)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        if (completed != task) return false;
        try { await task; } catch { /* already best-effort */ }
        return true;
    }

    private void OnPartial(string text)
    {
        lock (_finalsLock) { _partial = text; }
        _ui.BeginInvoke(() => _overlay?.SetText(ComposeTranscript()));
    }

    private void OnFinal(string segment)
    {
        lock (_finalsLock)
        {
            if (_finals.Length > 0 && NeedsSpace()) _finals.Append(' ');
            _finals.Append(segment);
            _partial = string.Empty;
        }
        _ui.BeginInvoke(() => _overlay?.SetText(ComposeTranscript()));
    }

    private void OnPcm(byte[] pcm) => _engine?.Feed(pcm);

    private void OnSpeechFault(SpeechFault fault)
    {
        _speechFault = fault;
        Log.Write($"Speech fault: {fault.Kind} - {fault.Detail ?? fault.UserMessage}");
    }

    // _finals/_partial are touched by engine callback threads and the UI thread; guard every access.
    private string ComposeTranscript()
    {
        lock (_finalsLock)
        {
            if (_partial.Length == 0) return _finals.ToString();
            if (_finals.Length == 0) return _partial;
            return _finals + (NeedsSpace() ? " " : string.Empty) + _partial;
        }
    }

    private void ClearTranscript()
    {
        lock (_finalsLock)
        {
            _finals.Clear();
            _partial = string.Empty;
        }
    }

    private bool NeedsSpace() =>
        !(_settings.Language.StartsWith("zh") || _settings.Language.StartsWith("ja") || _settings.Language.StartsWith("ko"));

    private ISpeechEngine CreateEngine(out bool azureFellBack)
    {
        azureFellBack = false;
        switch (_settings.Engine)
        {
            case SpeechEngineKind.Azure:
                if (_settings.AzureAuthMode == AzureAuthMode.EntraId)
                {
                    if (!string.IsNullOrWhiteSpace(_settings.AzureEndpoint))
                        return AzureSpeechEngine.ForEntra(
                            _settings.AzureEndpoint,
                            EntraCredentialFactory.Create(_settings.AzureTenantId));
                }
                else if (!string.IsNullOrWhiteSpace(_settings.AzureKey) && !string.IsNullOrWhiteSpace(_settings.AzureRegion))
                {
                    return AzureSpeechEngine.ForKey(_settings.AzureKey, _settings.AzureRegion);
                }
                azureFellBack = true;
                throw new InvalidOperationException(_settings.AzureAuthMode == AzureAuthMode.Key
                    ? "Azure Speech key authentication requires both Key and Region in Settings."
                    : "Azure Speech Entra authentication requires an HTTPS Endpoint in Settings.");

            case SpeechEngineKind.GptTranscribe:
                if (!string.IsNullOrWhiteSpace(_settings.TranscribeEndpoint) && !string.IsNullOrWhiteSpace(_settings.TranscribeModel))
                {
                    if (_settings.TranscribeAuthMode == AzureAuthMode.Key)
                    {
                        if (!string.IsNullOrWhiteSpace(_settings.TranscribeApiKey))
                            return OpenAiTranscribeEngine.ForKey(_settings.TranscribeEndpoint, _settings.TranscribeModel, _settings.TranscribeApiKey);
                    }
                    else
                    {
                        return OpenAiTranscribeEngine.ForEntra(
                            _settings.TranscribeEndpoint,
                            _settings.TranscribeModel,
                            EntraCredentialFactory.Create(_settings.TranscribeTenantId));
                    }
                }
                azureFellBack = true;
                if (string.IsNullOrWhiteSpace(_settings.TranscribeEndpoint))
                    throw new InvalidOperationException("Foundry transcription requires an HTTPS Endpoint in Settings.");
                if (string.IsNullOrWhiteSpace(_settings.TranscribeModel))
                    throw new InvalidOperationException("Foundry transcription requires a Deployment in Settings.");
                throw new InvalidOperationException("Foundry key authentication requires an API Key in Settings.");
        }
        return new WindowsSpeechEngine();
    }

    private void DisposeEngine()
    {
        if (_engine is null) return;
        if (_partialHandler is not null) _engine.Partial -= _partialHandler;
        if (_finalHandler is not null) _engine.Final -= _finalHandler;
        if (_faultHandler is not null) _engine.Fault -= _faultHandler;
        _partialHandler = null;
        _finalHandler = null;
        _faultHandler = null;
        if (_engine is OpenAiTranscribeEngine transcribe)
            transcribe.AuthExpired -= OnTranscribeAuthExpired;
        _engine.Dispose();
        _engine = null;
    }

    private volatile bool _reauthInFlight;

    /// <summary>The transcribe engine couldn't get an Entra token (sign-in expired). Re-authenticate
    /// in the background — off the dictation lock — so a browser popup never freezes dictation.</summary>
    private void OnTranscribeAuthExpired()
    {
        if (_reauthInFlight) return;
        _reauthInFlight = true;
        _ui.BeginInvoke(() =>
        {
            Notify("Azure sign-in expired",
                "Re-authenticating for transcription — a sign-in window may appear. Then just talk again.");
            _ = ReauthAsync();
        });
    }

    private async Task ReauthAsync()
    {
        try { await PrewarmEntraAsync(); }
        finally { _reauthInFlight = false; }
    }

    private string Placeholder() => _settings.Language.StartsWith("zh") ? "聆听中…" : "Listening…";

    private string Preparing() => _settings.Language.StartsWith("zh") ? "准备中…" : "Starting…";

    // ---------------- Tray ----------------

    private void BuildTray()
    {
        _trayIcon = TrayIconFactory.CreateMicIcon();
        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = TrayTooltip(),
        };
        RebuildMenu();
    }

    private string TrayTooltip() => _paused
        ? "VoiceInput — paused"
        : _session.State is not DictationSessionState.Idle
            ? $"VoiceInput — {_session.State.ToString().ToLowerInvariant()}"
            : $"VoiceInput — hold {PttDisplay(_settings.PttKey)} to talk";

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _session.Cancel();
            _audio.Release();   // drop any warm mic so paused == mic fully off
            if (_dictating) _ = CancelDictationAsync();
        }
        if (_tray is not null) _tray.Text = TrayTooltip();
        Notify(_paused ? "Listening paused" : "Listening resumed",
            _paused ? "VoiceInput won't respond to the push-to-talk key until resumed."
                    : "Push-to-talk is active again.");
        RebuildMenu();
    }

    private static string StartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VoiceInput.lnk");

    private static bool IsAutoStartEnabled() => File.Exists(StartupShortcutPath);

    private void SetAutoStart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string exe = Environment.ProcessPath!;
                string dir = Path.GetDirectoryName(exe)!;
                string ps = $"$s=New-Object -ComObject WScript.Shell; $l=$s.CreateShortcut('{StartupShortcutPath}'); " +
                            $"$l.TargetPath='{exe}'; $l.WorkingDirectory='{dir}'; $l.Description='VoiceInput'; $l.Save()";
                Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"")
                { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
            }
            else if (File.Exists(StartupShortcutPath))
            {
                File.Delete(StartupShortcutPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("SetAutoStart", ex);
        }
    }

    private static string PttDisplay(string key) => key switch
    {
        "RightCtrl" => "Right Ctrl",
        "LeftCtrl" => "Left Ctrl",
        "CapsLock" => "Caps Lock",
        "RightAlt" => "Right Alt",
        "RightShift" => "Right Shift",
        _ => key,
    };

    private void RebuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        var header = new WinForms.ToolStripMenuItem($"VoiceInput v{UpdateService.CurrentVersion}") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        if (!string.IsNullOrEmpty(_pendingText))
        {
            var retry = new WinForms.ToolStripMenuItem("Retry pending text");
            retry.Click += (_, _) => _ = RetryPendingTextAsync();
            menu.Items.Add(retry);
            var copy = new WinForms.ToolStripMenuItem("Copy pending text");
            copy.Click += (_, _) => CopyPendingText();
            menu.Items.Add(copy);
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

        var pause = new WinForms.ToolStripMenuItem(_paused ? "Resume listening" : "Pause listening");
        pause.Click += (_, _) => TogglePause();
        menu.Items.Add(pause);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Language
        var lang = new WinForms.ToolStripMenuItem("Language");
        foreach (var (code, display) in AppSettings.SupportedLanguages)
        {
            var item = new WinForms.ToolStripMenuItem(display) { Checked = _settings.Language == code };
            item.Click += (_, _) => { _settings.Language = code; Persist(); RebuildMenu(); };
            lang.DropDownItems.Add(item);
        }
        menu.Items.Add(lang);

        // Engine
        var engine = new WinForms.ToolStripMenuItem("Engine");
        AddRadio(engine, "Windows (on-device)", _settings.Engine == SpeechEngineKind.Windows,
            () => { _settings.Engine = SpeechEngineKind.Windows; Persist(); RebuildMenu(); });
        AddRadio(engine, "Azure Speech", _settings.Engine == SpeechEngineKind.Azure,
            () => { _settings.Engine = SpeechEngineKind.Azure; Persist(); RebuildMenu(); });
        AddRadio(engine, "gpt-4o-transcribe (Foundry)", _settings.Engine == SpeechEngineKind.GptTranscribe,
            () => { _settings.Engine = SpeechEngineKind.GptTranscribe; Persist(); RebuildMenu(); });
        menu.Items.Add(engine);

        // Push-to-talk key
        var ptt = new WinForms.ToolStripMenuItem("Push-to-talk key");
        foreach (var key in new[] { "RightCtrl", "LeftCtrl", "CapsLock", "RightAlt", "RightShift" })
        {
            string k = key;
            AddRadio(ptt, PttDisplay(k), _settings.PttKey == k, () =>
            {
                _settings.PttKey = k;
                _hook.SetPttKey(k);
                if (_tray is not null) _tray.Text = TrayTooltip();
                Persist();
                RebuildMenu();
            });
        }
        menu.Items.Add(ptt);

        // LLM refinement (enable toggle; full config lives in the top-level Settings window)
        var llm = new WinForms.ToolStripMenuItem("LLM Refinement");
        var enabled = new WinForms.ToolStripMenuItem("Enabled") { Checked = _settings.LlmEnabled };
        enabled.Click += (_, _) => ToggleSensitiveSetting(
            "Enable cloud LLM refinement?",
            "Dictated text will be sent to the configured LLM endpoint. If context is enabled, surrounding app text is sent too.",
            () => _settings.LlmEnabled = !_settings.LlmEnabled, _settings.LlmEnabled);
        llm.DropDownItems.Add(enabled);
        menu.Items.Add(llm);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Top-level Settings entry — holds Speech Engine (Azure key/region) AND LLM config.
        var settings = new WinForms.ToolStripMenuItem("Settings…  (engine / Azure / LLM)");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        var openLog = new WinForms.ToolStripMenuItem("Open log");
        openLog.Click += (_, _) => OpenUri(Log.FilePath);
        menu.Items.Add(openLog);

        var autostart = new WinForms.ToolStripMenuItem("Start at login") { Checked = IsAutoStartEnabled() };
        autostart.Click += (_, _) => { SetAutoStart(!IsAutoStartEnabled()); RebuildMenu(); };
        menu.Items.Add(autostart);

        var ctx = new WinForms.ToolStripMenuItem("Use surrounding context (UIA)") { Checked = _settings.UseContext };
        ctx.Click += (_, _) => ToggleSensitiveSetting(
            "Enable surrounding context?",
            "VoiceInput will read text from the focused app with UI Automation and send it to your configured LLM.",
            () => _settings.UseContext = !_settings.UseContext, _settings.UseContext);
        menu.Items.Add(ctx);

        var learn = new WinForms.ToolStripMenuItem("Learn from my edits") { Checked = _settings.LearnFromEdits };
        learn.Click += (_, _) => ToggleSensitiveSetting(
            "Enable edit learning?",
            "For up to two minutes after a successful insertion, Enter may capture the same input control. Up to 100 encrypted correction samples are stored locally.",
            () => _settings.LearnFromEdits = !_settings.LearnFromEdits, _settings.LearnFromEdits);
        menu.Items.Add(learn);

        var learnNow = new WinForms.ToolStripMenuItem("Learn from corrections…");
        learnNow.Click += (_, _) => LearnFromCorrections();
        menu.Items.Add(learnNow);

        var diag = new WinForms.ToolStripMenuItem("Log transcript text (diagnostic)") { Checked = _settings.DiagnosticLogging };
        diag.Click += (_, _) => ToggleSensitiveSetting(
            "Log dictated text?",
            "Full transcripts and LLM output may contain passwords or other sensitive data and will be written in plaintext to the diagnostic log.",
            () => _settings.DiagnosticLogging = !_settings.DiagnosticLogging, _settings.DiagnosticLogging);
        menu.Items.Add(diag);

        if (UpdateService.UpdatesEnabled)
        {
            var update = new WinForms.ToolStripMenuItem(
                _availableUpdateTag is null ? "Check for updates…" : $"Update to {_availableUpdateTag}…");
            update.Click += (_, _) =>
            {
                if (_availableUpdateTag is null) _ = CheckForUpdatesAsync(silent: false);
                else PromptAndApplyUpdate(_availableUpdateTag);
            };
            menu.Items.Add(update);
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var quit = new WinForms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        _tray!.ContextMenuStrip = menu;
    }

    private static void AddRadio(WinForms.ToolStripMenuItem parent, string text, bool check, Action onClick)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Checked = check };
        item.Click += (_, _) => onClick();
        parent.DropDownItems.Add(item);
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings, updated =>
        {
            _settings = updated;
            _store.Save(_settings);
            _hook.SetPttKey(_settings.PttKey);
            if (_tray is not null) _tray.Text = TrayTooltip();
            RebuildMenu();
        });
        win.Show();
        win.Activate();
    }

    private void Persist() => _store.Save(_settings);

    // ---------------- Updates (manual, user-chosen) ----------------

    private async Task CheckForUpdatesAsync(bool silent)
    {
        var result = await _updater.CheckAsync();
        _ = _ui.BeginInvoke(() =>
        {
            switch (result.Outcome)
            {
                case UpdateService.CheckOutcome.UpdateAvailable:
                    _availableUpdateTag = result.LatestTag;
                    _availableAssetUrl = result.AssetApiUrl;
                    Notify("Update available", $"{result.LatestTag} is available. Tray menu → Update to {result.LatestTag}…");
                    RebuildMenu();
                    break;
                case UpdateService.CheckOutcome.UpToDate:
                    _availableUpdateTag = null;
                    if (!silent) Notify("Up to date", $"You're on the latest version (v{UpdateService.CurrentVersion}).");
                    RebuildMenu();
                    break;
                case UpdateService.CheckOutcome.CheckFailed:
                    if (!silent)
                        Notify("Update check failed",
                            $"Couldn't reach GitHub Releases for {UpdateService.Repo}. Check your connection and try again.");
                    break;
                case UpdateService.CheckOutcome.UpdatesDisabled:
                    if (!silent)
                        Notify("Updates unavailable",
                            "This development build is not signed by a pinned publisher. Install an official release to enable updates.");
                    break;
            }
        });
    }

    private void PromptAndApplyUpdate(string tag)
    {
        if (_availableAssetUrl is null)
        {
            Notify("Update unavailable", $"{tag} has no downloadable VoiceInput.exe asset.");
            return;
        }

        var answer = MessageBox.Show(
            $"Update to {tag} now?\n\nVoiceInput will download the new version and restart.",
            "VoiceInput update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Notify("Updating…", $"Downloading {tag}. The app will restart when it's ready.");
        _ = ApplyUpdateAsync(tag);
    }

    private async Task ApplyUpdateAsync(string tag)
    {
        bool ok = _availableAssetUrl is not null && await _updater.DownloadAndApplyAsync(_availableAssetUrl);
        _ = _ui.BeginInvoke(() =>
        {
            if (ok) Application.Current.Shutdown();   // detached helper replaces the exe and relaunches
            else Notify("Update failed",
                $"Couldn't download {tag}. Check your connection and try again.");
        });
    }

    // ---------------- Learn from edits ----------------

    private void LearnFromCorrections()
    {
        var pairs = _corrections.LoadPairs();
        if (pairs.Count < 3)
        {
            Notify("Not enough edits yet", $"{pairs.Count} captured. Keep dictating + editing (needs ~3+, and 'Learn from my edits' on).");
            return;
        }
        Notify("Learning…", $"Summarizing {pairs.Count} corrections.");
        _ = LearnAsync(pairs);
    }

    private async Task LearnAsync(System.Collections.Generic.List<(string Raw, string Refined, string Edited)> pairs)
    {
        string rules;
        try { rules = await _refiner.SummarizeCorrectionsAsync(pairs, _settings); }
        catch (Exception ex) { _ = _ui.BeginInvoke(() => Notify("Learn failed", ex.Message)); return; }

        _ = _ui.BeginInvoke(() =>
        {
            if (string.IsNullOrWhiteSpace(rules)) { Notify("Nothing learned", "No recurring patterns found."); return; }
            var ans = MessageBox.Show(
                $"Apply these learned correction rules to your refine prompt?\n\n{rules}",
                "VoiceInput — learned rules", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes)
            {
                _settings.LlmLearnedRules = rules;
                _store.Save(_settings);
                _corrections.Clear();
                Notify("Applied", "Learned rules added to your refine prompt.");
            }
        });
    }

    private void Notify(string title, string message) =>
        _tray?.ShowBalloonTip(6000, title, message, WinForms.ToolTipIcon.Info);

    private void ToggleSensitiveSetting(string title, string warning, Action toggle, bool currentlyEnabled)
    {
        if (!currentlyEnabled && MessageBox.Show(warning, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        toggle();
        Persist();
        RebuildMenu();
    }

    private void PreserveText(string text, string reason)
    {
        _pendingText = text;
        CopyPendingText();
        Notify("Text was not inserted",
            $"{reason} The remaining text was preserved and copied to the clipboard; use the tray menu to retry.");
        RebuildMenu();
    }

    private void CopyPendingText()
    {
        if (string.IsNullOrEmpty(_pendingText)) return;
        try { Clipboard.SetText(_pendingText); }
        catch (Exception ex) { Log.Error("Copy pending text", ex); }
    }

    private async Task RetryPendingTextAsync()
    {
        string? text = _pendingText;
        if (string.IsNullOrEmpty(text)) return;
        var result = await _injector.InjectAsync(text, _injector.CaptureTarget());
        if (!result.Success)
        {
            HandleInjectionFailure(text, result);
            return;
        }
        _pendingText = null;
        Notify("Text inserted", "The preserved text was inserted into the current control.");
        RebuildMenu();
    }

    private void HandleInjectionFailure(string text, TextInjector.Result result)
    {
        int inserted = Math.Clamp(result.CharactersInserted, 0, text.Length);
        if (inserted < text.Length)
        {
            PreserveText(text[inserted..], result.Error ?? "Windows rejected text injection.");
            return;
        }
        _pendingText = null;
        Notify("Text injection warning",
            $"{result.Error ?? "Windows reported an incomplete injection."} No characters remain to retry.");
        RebuildMenu();
    }

    private static bool IsPrivacyPolicyError(Exception ex) =>
        ex.Message.Contains("privacy", StringComparison.OrdinalIgnoreCase);

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* settings deep-link unavailable */ }
    }

    public void Dispose()
    {
        _session.Dispose();
        _hook.Dispose();
        _audio.Dispose();
        DisposeEngine();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
    }
}
