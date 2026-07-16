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

internal enum PttGesture { Engaged, Released, Cancelled }
internal enum PttGestureAction { None, Start, Stop, Cancel, Busy }

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
    private readonly FunAsrRuntimeManager _funAsr = new();
    private readonly object _funAsrInstallLock = new();
    private CancellationTokenSource? _funAsrInstallCancellation;
    private Task? _funAsrInstallTask;
    private string? _funAsrInstallingModelId;
    private string? _availableUpdateTag;
    private string? _availableAssetUrl;
    private string? _availableAssetSha256;

    private OverlayWindow? _overlay;
    private FirstRunWindow? _firstRunWindow;
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
        _hook.Engaged += () => _ui.BeginInvoke(() => HandlePttGesture(PttGesture.Engaged));
        _hook.Released += () => _ui.BeginInvoke(() => HandlePttGesture(PttGesture.Released));
        _hook.Cancelled += () => _ui.BeginInvoke(() => HandlePttGesture(PttGesture.Cancelled));
        _hook.Submitted += () => _ui.BeginInvoke(() => _ = _corrections.CaptureAsync(_contextReader, _injector));

        _audio.LevelChanged += lvl => { _level = lvl; if (lvl > _sessionPeak) _sessionPeak = lvl; };
    }

    internal static PttGestureAction ResolvePttGesture(
        PttMode mode,
        PttGesture gesture,
        bool dictating,
        DictationSessionState state)
    {
        bool busy = state is DictationSessionState.Transcribing
            or DictationSessionState.Refining
            or DictationSessionState.Injecting;

        if (mode == PttMode.Toggle)
        {
            if (gesture != PttGesture.Released) return PttGestureAction.None;
            if (busy) return PttGestureAction.Busy;
            return dictating ? PttGestureAction.Stop : PttGestureAction.Start;
        }

        return gesture switch
        {
            PttGesture.Engaged when busy => PttGestureAction.Busy,
            PttGesture.Engaged => PttGestureAction.Start,
            PttGesture.Released => PttGestureAction.Stop,
            PttGesture.Cancelled => PttGestureAction.Cancel,
            _ => PttGestureAction.None,
        };
    }

    internal static PttMode ResolvePttModeAfterSettingsSave(
        PttMode current,
        PttMode settingsOpenedWith,
        PttMode submitted) =>
        submitted == settingsOpenedWith ? current : submitted;

    internal static (SpeechEngineKind Engine, string ModelId) ResolveOnboardingRecognition(
        FirstRunCompletionChoice choice,
        SpeechEngineKind currentEngine,
        string currentModelId) => choice switch
        {
            FirstRunCompletionChoice.DefaultLocal =>
                (SpeechEngineKind.FunAsr, FunAsrModelCatalog.DefaultId),
            FirstRunCompletionChoice.WindowsFallback =>
                (SpeechEngineKind.Windows, currentModelId),
            _ => (currentEngine, currentModelId),
        };

    private void HandlePttGesture(PttGesture gesture)
    {
        switch (ResolvePttGesture(_settings.PttMode, gesture, _dictating, _session.State))
        {
            case PttGestureAction.Start:
                _ = StartDictationAsync();
                break;
            case PttGestureAction.Stop:
                _ = StopDictationAsync();
                break;
            case PttGestureAction.Cancel:
                _ = CancelDictationAsync();
                break;
            case PttGestureAction.Busy:
                Notify("Still processing", "Wait for the current dictation to finish before starting another.");
                break;
        }
    }

    public void Start()
    {
        _overlay = new OverlayWindow { LevelSource = () => _level };
        BuildTray();
        _hook.Install();
        Log.Write($"=== VoiceInput v{UpdateService.CurrentVersion} started. ptt={_settings.PttKey}, pttMode={_settings.PttMode}, engine={_settings.Engine}, lang={_settings.Language}, llm={_settings.LlmEnabled} ===");
        Log.Write("Windows dictation languages available: " + WindowsSpeechEngine.SupportedTopicLanguageTags());

        if (!_settings.OnboardingCompleted) ShowFirstRunOnboarding();

        _ = CheckForUpdatesAsync(silent: true);   // notify if a newer release exists; never auto-applies
        _ = PrewarmEntraAsync();                  // sign in once up front so dictation never triggers a focus-stealing popup
    }

    /// <summary>
    /// First launch on a machine: persist safe defaults, then teach the real push-to-talk flow.
    /// Closing the guide does not mark it complete, so the tray keeps a recovery entry.
    /// </summary>
    private void ShowFirstRunOnboarding()
    {
        Log.Write("First run — showing onboarding.");
        Persist();
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenFirstRun));
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

            case SpeechEngineKind.FunAsr:
                FunAsrModelDefinition model = FunAsrModelCatalog.Get(_settings.FunAsrModelId);
                if (!model.Supports(_settings.Language))
                {
                    throw new InvalidOperationException(
                        $"{model.DisplayName} does not support {_settings.Language}. Choose a compatible model or language in Setup.");
                }
                if (!_funAsr.IsInstalled(model.Id))
                {
                    throw new InvalidOperationException(
                        "Download the selected FunASR model in Setup before using it.");
                }
                return new FunAsrEngine(_funAsr.Resolve(model.Id));
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
            : _settings.PttMode == PttMode.Toggle
                ? $"VoiceInput — press {PttDisplay(_settings.PttKey)} to start/stop"
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
            _paused ? "VoiceInput won't respond to the activation key until resumed."
                    : "Voice input activation is active again.");
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

    private static string FirstRunRecognitionSummary(AppSettings settings, string modelId) =>
        settings.Engine switch
        {
            SpeechEngineKind.FunAsr => $"FunASR 本地 · {FunAsrModelCatalog.Get(modelId).DisplayName}",
            SpeechEngineKind.Azure => "Azure Speech 已配置",
            SpeechEngineKind.GptTranscribe => $"GPT-4o Transcribe · {settings.TranscribeModel}",
            _ => "Windows 听写 · 准确率较低",
        };

    private void RebuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        if (!_settings.OnboardingCompleted)
        {
            var setup = new WinForms.ToolStripMenuItem("完成快速设置…");
            setup.Click += (_, _) => OpenFirstRun();
            menu.Items.Add(setup);
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

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

        var learnNow = new WinForms.ToolStripMenuItem("Learn from corrections…");
        learnNow.Click += (_, _) => LearnFromCorrections();
        menu.Items.Add(learnNow);

        var settings = new WinForms.ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        if (_availableUpdateTag is { } updateTag)
        {
            var update = new WinForms.ToolStripMenuItem($"Update to {updateTag}…");
            update.Click += (_, _) => PromptAndApplyUpdate(updateTag);
            menu.Items.Add(update);
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var quit = new WinForms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        _tray!.ContextMenuStrip = menu;
    }

    private void OpenFirstRun()
    {
        if (_firstRunWindow is { IsVisible: true })
        {
            _firstRunWindow.Activate();
            return;
        }

        string selectedModelId = FunAsrModelCatalog.NormalizeId(_settings.FunAsrModelId);
        bool useConfiguredRecognition = _settings.Engine != SpeechEngineKind.FunAsr
            || selectedModelId != FunAsrModelCatalog.DefaultId;
        bool recognitionReady = _settings.Engine != SpeechEngineKind.FunAsr
            || _funAsr.IsInstalled(selectedModelId);
        var window = new FirstRunWindow(
            _settings.PttKey,
            PttDisplay(_settings.PttKey),
            _settings.PttMode,
            new FirstRunWindowActions(
                recognitionReady,
                useConfiguredRecognition,
                FirstRunRecognitionSummary(_settings, selectedModelId),
                InstallDefaultFunAsrForOnboardingAsync,
                () => CancelFunAsrInstall(FunAsrModelCatalog.DefaultId),
                ConfirmWindowsFallback,
                () => _hook.IsPttGestureChorded,
                SetOnboardingPttMode,
                CompleteOnboarding,
                OpenSettings));
        _firstRunWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_firstRunWindow, window))
                _firstRunWindow = null;
        };
        window.Show();
        window.Activate();
    }

    private static bool ConfirmWindowsFallback() => MessageBox.Show(
        "Windows 听写的识别准确率较低，尤其是中文、口音和专业词汇。\n\n" +
        "建议下载并使用 FunASR 本地模型。仍要改用 Windows 听写吗？",
        "改用 Windows 听写？",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void SetOnboardingPttMode(PttMode mode)
    {
        if (_settings.PttMode == mode)
            return;
        _settings.PttMode = mode;
        Persist();
        if (_tray is not null)
            _tray.Text = TrayTooltip();
        Log.Write($"First-run activation mode changed to {mode}.");
    }

    private bool CompleteOnboarding(FirstRunCompletionChoice choice)
    {
        if (_settings.OnboardingCompleted) return true;
        (SpeechEngineKind engine, string modelId) = ResolveOnboardingRecognition(
            choice,
            _settings.Engine,
            FunAsrModelCatalog.NormalizeId(_settings.FunAsrModelId));
        if (engine == SpeechEngineKind.FunAsr && !_funAsr.IsInstalled(modelId))
            return false;

        _settings.Engine = engine;
        _settings.FunAsrModelId = modelId;
        _settings.OnboardingCompleted = true;
        Persist();
        RebuildMenu();
        Log.Write($"First-run onboarding completed with engine={_settings.Engine}.");
        return true;
    }

    private async Task InstallDefaultFunAsrForOnboardingAsync(
        Action<FunAsrInstallProgress> reportProgress)
    {
        _funAsr.ProgressChanged += reportProgress;
        try
        {
            await InstallFunAsrAsync(FunAsrModelCatalog.DefaultId);
        }
        finally
        {
            _funAsr.ProgressChanged -= reportProgress;
        }
    }

    private void OpenSettings()
    {
        FirstRunWindow? firstRun = _firstRunWindow;
        if (firstRun is { IsVisible: true })
            firstRun.IsEnabled = false;
        PttMode settingsOpenedWith = _settings.PttMode;
        var win = new SettingsWindow(_settings, updated =>
        {
            updated.PttMode = ResolvePttModeAfterSettingsSave(
                _settings.PttMode,
                settingsOpenedWith,
                updated.PttMode);
            _settings = updated;
            _store.Save(_settings);
            _hook.SetPttKey(_settings.PttKey);
            if (_tray is not null) _tray.Text = TrayTooltip();
            RebuildMenu();
        }, _funAsr, new SettingsWindowActions(
            IsAutoStartEnabled,
            SetAutoStart,
            () => CheckForUpdatesAsync(silent: false),
            PromptAndApplyUpdate,
            () => OpenUri(Log.FilePath),
            InstallFunAsrAsync,
            CancelFunAsrInstall,
            ActiveFunAsrModelId));
        if (firstRun is not null)
        {
            win.Owner = firstRun;
            win.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_firstRunWindow, firstRun))
                    return;
                firstRun.Close();
                if (!_settings.OnboardingCompleted)
                    OpenFirstRun();
            };
        }
        win.Show();
        win.Activate();
    }

    private Task InstallFunAsrAsync(string modelId)
    {
        lock (_funAsrInstallLock)
        {
            if (_funAsrInstallTask is { IsCompleted: false })
            {
                if (_funAsrInstallingModelId == modelId)
                    return _funAsrInstallTask;
                throw new InvalidOperationException(
                    $"Finish or cancel the {_funAsrInstallingModelId} download before starting another model.");
            }

            var cancellation = new CancellationTokenSource();
            Task install = _funAsr.InstallAsync(modelId, cancellation.Token);
            _funAsrInstallCancellation = cancellation;
            _funAsrInstallingModelId = modelId;
            _funAsrInstallTask = ObserveFunAsrInstallAsync(modelId, cancellation, install);
            return _funAsrInstallTask;
        }
    }

    private async Task ObserveFunAsrInstallAsync(
        string modelId, CancellationTokenSource cancellation, Task install)
    {
        try
        {
            await install;
        }
        finally
        {
            lock (_funAsrInstallLock)
            {
                if (ReferenceEquals(_funAsrInstallCancellation, cancellation))
                {
                    _funAsrInstallCancellation = null;
                    _funAsrInstallingModelId = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private void CancelFunAsrInstall(string modelId)
    {
        lock (_funAsrInstallLock)
        {
            if (_funAsrInstallingModelId == modelId)
                _funAsrInstallCancellation?.Cancel();
        }
    }

    private string? ActiveFunAsrModelId()
    {
        lock (_funAsrInstallLock)
            return _funAsrInstallTask is { IsCompleted: false } ? _funAsrInstallingModelId : null;
    }

    private void Persist() => _store.Save(_settings);

    // ---------------- Updates (manual, user-chosen) ----------------

    private async Task<UpdateService.CheckResult> CheckForUpdatesAsync(bool silent)
    {
        var result = await _updater.CheckAsync();
        await _ui.InvokeAsync(() =>
        {
            switch (result.Outcome)
            {
                case UpdateService.CheckOutcome.UpdateAvailable:
                    _availableUpdateTag = result.LatestTag;
                    _availableAssetUrl = result.AssetApiUrl;
                    _availableAssetSha256 = result.AssetSha256;
                    if (silent)
                        Notify("Update available", $"{result.LatestTag} is available. Tray menu → Update to {result.LatestTag}…");
                    break;
                case UpdateService.CheckOutcome.UpToDate:
                    _availableUpdateTag = null;
                    _availableAssetUrl = null;
                    _availableAssetSha256 = null;
                    break;
                case UpdateService.CheckOutcome.CheckFailed:
                    break;
            }
            RebuildMenu();
        });
        return result;
    }

    private void PromptAndApplyUpdate(string tag)
    {
        if (_availableAssetUrl is null ||
            !UpdateService.UsesPinnedPublisherVerification && _availableAssetSha256 is null)
        {
            Notify("Update unavailable", $"{tag} has no verifiable VoiceInput.exe asset.");
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
        bool ok = _availableAssetUrl is not null &&
            await _updater.DownloadAndApplyAsync(_availableAssetUrl, _availableAssetSha256);
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
        _firstRunWindow?.Close();
        Task? installTask;
        lock (_funAsrInstallLock)
        {
            _funAsrInstallCancellation?.Cancel();
            installTask = _funAsrInstallTask;
        }
        if (installTask is { IsCompleted: false })
        {
            try
            {
                if (!installTask.Wait(TimeSpan.FromSeconds(5)))
                    Log.Write("WARN FunASR installation did not stop within the shutdown timeout.");
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(
                inner => inner is OperationCanceledException))
            {
            }
            catch (AggregateException exception)
            {
                Log.Error("FunASR install shutdown", exception.Flatten().InnerException ?? exception);
            }
        }
        _session.Dispose();
        _hook.Dispose();
        _audio.Dispose();
        DisposeEngine();
        _funAsr.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
    }
}
