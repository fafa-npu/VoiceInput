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

internal enum PttGesture { Engaged, Released, Cancelled, RecoveredRelease }
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
    private readonly object _updateLock = new();
    private CancellationTokenSource? _funAsrInstallCancellation;
    private Task? _funAsrInstallTask;
    private string? _funAsrInstallingModelId;
    private Task<UpdateService.CheckResult>? _updateCheckTask;
    private Task? _updateApplyTask;
    private UpdateCandidate? _availableUpdate;

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

    private sealed record UpdateCandidate(string Tag, string? AssetUrl, string? AssetSha256);

    public AppController()
    {
        _ui = Application.Current.Dispatcher;
        _settings = _store.Load();

        _hook = new KeyboardHook(_settings.PttKey);
        _hook.Engaged += () => _ui.BeginInvoke(() => HandlePttGesture(PttGesture.Engaged));
        _hook.Released += () => _ui.BeginInvoke(() => HandlePttGesture(PttGesture.Released));
        // RecoveredRelease is raised by the UI-thread watchdog. Handle it immediately so a missed
        // key-up cannot sit in the dispatcher queue behind the user's next Ctrl gesture.
        _hook.RecoveredRelease += () => HandlePttGesture(PttGesture.RecoveredRelease);
        _hook.Cancelled += () => _ui.BeginInvoke(() => HandlePttGesture(PttGesture.Cancelled));
        _hook.EscapePressed += () => _ui.BeginInvoke(HandleEscapePressed);
        _hook.Submitted += () => _ui.BeginInvoke(() => _ = _corrections.CaptureAsync(_contextReader, _injector));
        _hook.ProfileSwitchRequested += () => _ui.BeginInvoke(HandleProfileSwitchRequested);

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
            if (gesture is not (PttGesture.Released or PttGesture.RecoveredRelease))
                return PttGestureAction.None;
            if (busy) return PttGestureAction.Busy;
            return dictating ? PttGestureAction.Stop : PttGestureAction.Start;
        }

        return gesture switch
        {
            PttGesture.Engaged when busy => PttGestureAction.Busy,
            PttGesture.Engaged => PttGestureAction.Start,
            PttGesture.Released or PttGesture.RecoveredRelease => PttGestureAction.Stop,
            PttGesture.Cancelled => PttGestureAction.Cancel,
            _ => PttGestureAction.None,
        };
    }

    internal static PttMode ResolvePttModeAfterSettingsSave(
        PttMode current,
        PttMode settingsOpenedWith,
        PttMode submitted) =>
        submitted == settingsOpenedWith ? current : submitted;

    internal static string ResolveActiveProfileAfterSettingsSave(
        string current,
        string settingsOpenedWith,
        string submitted) =>
        submitted == settingsOpenedWith ? current : submitted;

    internal static bool CanSwitchProfile(bool dictating, DictationSessionState state) =>
        !dictating && state is not (
            DictationSessionState.Starting
            or DictationSessionState.Listening
            or DictationSessionState.Transcribing
            or DictationSessionState.Refining
            or DictationSessionState.Injecting);

    internal static bool ShouldCancelForEscape(bool dictating, DictationSessionState state) =>
        dictating && state is DictationSessionState.Starting or DictationSessionState.Listening;

    internal static string NextProfileId(string current) =>
        current == InputProfile.Profile2Id ? InputProfile.Profile1Id : InputProfile.Profile2Id;

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
                _ = CancelDictationAsync("chord detected");
                break;
            case PttGestureAction.Busy:
                Notify("Still processing", "Wait for the current dictation to finish before starting another.");
                break;
        }
    }

    private void HandleEscapePressed()
    {
        if (!ShouldCancelForEscape(_dictating, _session.State))
            return;

        // Signal first: StartDictationAsync may currently own the lifecycle gate while waiting on
        // model/microphone startup, and its token must be cancelled without waiting for that gate.
        Log.Write("Dictation cancelled (Escape pressed).");
        _session.Cancel();
        _engine?.Cancel();
        _overlay?.HideAnimated();
        _ = CancelDictationAsync(reason: null);
    }

    public void Start()
    {
        _overlay = new OverlayWindow
        {
            LevelSource = () => _level,
            Position = _settings.ActiveProfile.OverlayPosition,
        };
        RefreshInstalledUninstaller();
        MigrateLegacyShortcuts();
        BuildTray();
        _hook.Install();
        Log.Write($"=== gujiguji v{UpdateService.CurrentVersion} started. profile={_settings.ActiveProfile.Name}, ptt={_settings.PttKey}, pttMode={_settings.PttMode}, overlay={_settings.ActiveProfile.OverlayPosition}, engine={_settings.Engine}, lang={_settings.Language}, llm={_settings.LlmEnabled} ===");
        Log.Write("Windows dictation languages available: " + WindowsSpeechEngine.SupportedTopicLanguageTags());

        if (!_settings.OnboardingCompleted) ShowFirstRunOnboarding();

        _ = CheckForUpdatesAsync(silent: true);   // notify if a newer release exists; never auto-applies
        _ = PrewarmEntraAsync();                  // sign in once up front so dictation never triggers a focus-stealing popup
        PreverifySelectedLocalModel();            // hash large model files without freezing the first PTT UI frame
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

                await VerifySelectedLocalModelAsync(ct);

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
                bool live = await Task.WhenAny(_audio.FirstFrame, Task.Delay(FirstFrameTimeoutMs, ct)) == _audio.FirstFrame;
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
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Some recognizers surface their own exception type when Cancel interrupts
                // StartAsync. Escape cancellation is intentional and must stay silent.
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
                        "gujiguji didn't capture any sound. If you're in a Teams/other call, it may be holding the mic — you both share one redirected microphone.");
                _overlay!.HideAnimated();
                return;
            }

            string raw = text;   // raw STT, before LLM refine — recorded for correction learning
            var target = _inputTarget;
            if (target is null || !_injector.IsCurrentTarget(target))
            {
                PreserveText(text, "The focused window or input control changed while gujiguji was processing.");
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
            if (_settings.LearnFromEdits)
                _corrections.Arm(raw, text, target);
            _session.MoveTo(DictationSessionState.Idle);
        }
        finally
        {
            _session.CompleteProcessing();
            _gate.Release();
        }
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private async Task CancelDictationAsync(string? reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_dictating) return;
            if (reason is not null)
                Log.Write($"Dictation cancelled ({reason}).");
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
            if (!stopped)
            {
                Log.Write("WARN engine.StopAsync timed out; forcing dispose.");
                _speechFault ??= new(
                    SpeechFaultKind.Timeout,
                    "Transcription timed out. Try a shorter recording or a faster local model.",
                    "The native recognizer may continue finishing the discarded request in the background.");
            }
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
        RecognitionVocabularyEvaluation vocabulary = RecognitionVocabulary.Evaluate(_settings);
        Log.Write(RecognitionVocabulary.FormatSessionLog(_settings, vocabulary));
        azureFellBack = false;
        switch (_settings.Engine)
        {
            case SpeechEngineKind.Azure:
                if (_settings.AzureAuthMode == AzureAuthMode.EntraId)
                {
                    if (!string.IsNullOrWhiteSpace(_settings.AzureEndpoint))
                        return AzureSpeechEngine.ForEntra(
                            _settings.AzureEndpoint,
                            EntraCredentialFactory.Create(_settings.AzureTenantId),
                            vocabulary.Entries);
                }
                else if (!string.IsNullOrWhiteSpace(_settings.AzureKey) && !string.IsNullOrWhiteSpace(_settings.AzureRegion))
                {
                    return AzureSpeechEngine.ForKey(
                        _settings.AzureKey,
                        _settings.AzureRegion,
                        vocabulary.Entries);
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
                            return OpenAiTranscribeEngine.ForKey(
                                _settings.TranscribeEndpoint,
                                _settings.TranscribeModel,
                                _settings.TranscribeApiKey,
                                vocabulary.Entries);
                    }
                    else
                    {
                        return OpenAiTranscribeEngine.ForEntra(
                            _settings.TranscribeEndpoint,
                            _settings.TranscribeModel,
                            EntraCredentialFactory.Create(_settings.TranscribeTenantId),
                            vocabulary.Entries);
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
                        "Download the selected local model in Settings before using it.");
                }
                FunAsrResolvedModel resolved = _funAsr.Resolve(model.Id);
                return model.Runner == FunAsrRunnerKind.Qwen3Asr
                    ? new Qwen3AsrEngine(resolved, _funAsr.TranscribeQwen3Async, vocabulary.Entries)
                    : new FunAsrEngine(resolved);
        }
        return new WindowsSpeechEngine();
    }

    private async Task VerifySelectedLocalModelAsync(CancellationToken cancellationToken)
    {
        if (_settings.Engine != SpeechEngineKind.FunAsr)
            return;
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(_settings.FunAsrModelId);
        if (!_funAsr.HasInstalledFiles(model.Id))
            return; // CreateEngine reports the normal not-installed error.
        bool verified = await _funAsr.VerifyInstalledAsync(model.Id).WaitAsync(cancellationToken);
        if (!verified)
        {
            throw new InvalidDataException(
                $"{model.DisplayName} failed its integrity check. Remove and download the model again in Settings.");
        }
    }

    private void PreverifySelectedLocalModel()
    {
        if (_settings.Engine != SpeechEngineKind.FunAsr)
            return;
        string modelId = FunAsrModelCatalog.NormalizeId(_settings.FunAsrModelId);
        if (!_funAsr.HasInstalledFiles(modelId))
            return;
        _ = _funAsr.VerifyInstalledAsync(modelId).ContinueWith(
            task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    Log.Write(task.Result
                        ? $"Local model verification ready: {modelId}."
                        : $"WARN local model verification failed: {modelId}.");
                    if (task.Result
                        && FunAsrModelCatalog.Get(modelId).Runner == FunAsrRunnerKind.Qwen3Asr)
                    {
                        _ = PrewarmQwen3Async(modelId);
                    }
                }
                else if (task.Exception is not null)
                    Log.Error("Local model background verification", task.Exception.GetBaseException());
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PrewarmQwen3Async(string modelId)
    {
        try
        {
            await _funAsr.PrewarmQwen3Async(modelId, CancellationToken.None);
            Log.Write($"Qwen3-ASR recognizer pre-warmed: {modelId}.");
        }
        catch (ObjectDisposedException)
        {
            // Normal if the app exits while background preparation is queued.
        }
        catch (Exception exception)
        {
            Log.Error("Qwen3-ASR background pre-warm", exception);
        }
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
        _trayIcon = TrayIconFactory.CreateVoiceCursorIcon();
        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = TrayTooltip(),
        };
        RebuildMenu();
    }

    private string TrayTooltip()
    {
        string profile = _settings.ActiveProfile.Name;
        string state = _paused
            ? "paused"
            : _session.State is not DictationSessionState.Idle
                ? _session.State.ToString().ToLowerInvariant()
                : _settings.PttMode == PttMode.Toggle
                    ? $"{PttDisplay(_settings.PttKey)} start/stop"
                    : $"hold {PttDisplay(_settings.PttKey)}";
        string tooltip = $"gujiguji · {profile} · {state}";
        return tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void HandleProfileSwitchRequested() =>
        ActivateProfile(NextProfileId(_settings.ActiveProfileId), showOverlay: true);

    private void ActivateProfile(string profileId, bool showOverlay)
    {
        string normalized = InputProfile.NormalizeId(profileId);
        if (normalized == _settings.ActiveProfileId)
            return;
        if (!CanSwitchProfile(_dictating, _session.State))
        {
            Notify("Profile not switched", "Finish the current dictation before switching input profiles.");
            return;
        }

        _settings.ActiveProfileId = normalized;
        Persist();
        ApplyActiveProfile(showOverlay);
        Log.Write($"Input profile switched to id={normalized} name={_settings.ActiveProfile.Name} " +
            $"key={_settings.PttKey} mode={_settings.PttMode} overlay={_settings.ActiveProfile.OverlayPosition}.");
    }

    private void ApplyActiveProfile(bool showOverlay)
    {
        InputProfile profile = _settings.ActiveProfile;
        _hook.SetPttKey(profile.PttKey);
        if (_overlay is not null)
        {
            _overlay.Position = profile.OverlayPosition;
            if (showOverlay)
            {
                string behavior = profile.PttMode == PttMode.Toggle ? "press to start / stop" : "hold to talk";
                _overlay.ShowProfileChanged(profile.Name, $"{PttDisplay(profile.PttKey)} · {behavior}");
            }
        }
        if (_tray is not null)
            _tray.Text = TrayTooltip();
        RebuildMenu();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _session.Cancel();
            _audio.Release();   // drop any warm mic so paused == mic fully off
            if (_dictating) _ = CancelDictationAsync("listening paused");
        }
        if (_tray is not null) _tray.Text = TrayTooltip();
        Notify(_paused ? "Listening paused" : "Listening resumed",
            _paused ? "gujiguji won't respond to the activation key until resumed."
                    : "Voice input activation is active again.");
        RebuildMenu();
    }

    private static string StartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "gujiguji.lnk");

    private static string LegacyStartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VoiceInput.lnk");

    private static bool IsAutoStartEnabled() =>
        File.Exists(StartupShortcutPath) || File.Exists(LegacyStartupShortcutPath);

    private static void MigrateLegacyShortcuts()
    {
        foreach (Environment.SpecialFolder folder in
                 new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.Programs })
        {
            try { MigrateLegacyShortcut(Environment.GetFolderPath(folder)); }
            catch (Exception ex) { Log.Error("MigrateLegacyShortcut", ex); }
        }
    }

    private static void RefreshInstalledUninstaller()
    {
        try
        {
            string expectedDirectory = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoiceInput"));
            string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (processDirectory is null
                || !Path.GetFullPath(processDirectory).Equals(expectedDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            using Stream source = typeof(AppController).Assembly
                .GetManifestResourceStream("gujiguji.install.ps1")
                ?? throw new InvalidOperationException("Embedded installer script was not found.");
            using FileStream destination = File.Create(Path.Combine(processDirectory, "uninstall.ps1"));
            source.CopyTo(destination);
        }
        catch (Exception ex)
        {
            Log.Error("RefreshInstalledUninstaller", ex);
        }
    }

    internal static void MigrateLegacyShortcut(string directory)
    {
        string legacy = Path.Combine(directory, "VoiceInput.lnk");
        if (!File.Exists(legacy)) return;

        string branded = Path.Combine(directory, "gujiguji.lnk");
        if (File.Exists(branded)) File.Delete(legacy);
        else File.Move(legacy, branded);
    }

    private void SetAutoStart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string exe = Environment.ProcessPath!;
                string dir = Path.GetDirectoryName(exe)!;
                string ps = $"$s=New-Object -ComObject WScript.Shell; $l=$s.CreateShortcut('{StartupShortcutPath}'); " +
                            $"$l.TargetPath='{exe}'; $l.WorkingDirectory='{dir}'; $l.Description='gujiguji'; $l.Save()";
                Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"")
                { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
                MigrateLegacyShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            }
            else
            {
                if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
                if (File.Exists(LegacyStartupShortcutPath)) File.Delete(LegacyStartupShortcutPath);
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
            SpeechEngineKind.FunAsr => $"本地模型 · {FunAsrModelCatalog.Get(modelId).DisplayName}",
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

        var profileMenu = new WinForms.ToolStripMenuItem("Input profile");
        foreach (InputProfile profile in _settings.Profiles)
        {
            var item = new WinForms.ToolStripMenuItem(profile.Name)
            {
                Checked = profile.Id == _settings.ActiveProfileId,
            };
            item.Click += (_, _) => ActivateProfile(profile.Id, showOverlay: true);
            profileMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(profileMenu);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var intelligence = new WinForms.ToolStripMenuItem("Language intelligence…");
        intelligence.ToolTipText = "Open gujiguji — language intelligence settings.";
        intelligence.Click += (_, _) => OpenSettings(showLanguageIntelligence: true);
        menu.Items.Add(intelligence);

        var settings = new WinForms.ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        if (_availableUpdate?.Tag is { } updateTag)
        {
            var update = new WinForms.ToolStripMenuItem($"Update to {updateTag}…");
            update.Click += (_, _) => PromptAndApplyUpdate(updateTag);
            menu.Items.Add(update);
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var quit = new WinForms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        WinForms.ContextMenuStrip? previous = _tray!.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        if (previous is not null)
            _ui.BeginInvoke(DispatcherPriority.Background, new Action(previous.Dispose));
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
                InstallDefaultLocalModelForOnboardingAsync,
                () => CancelFunAsrInstall(FunAsrModelCatalog.DefaultId),
                ConfirmWindowsFallback,
                () => _hook.IsPttGestureChorded,
                SetOnboardingPttMode,
                CompleteOnboarding,
                () => OpenSettings()));
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
        "建议下载并使用本地模型。仍要改用 Windows 听写吗？",
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

    private async Task InstallDefaultLocalModelForOnboardingAsync(
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

    private void OpenSettings(bool showLanguageIntelligence = false)
    {
        FirstRunWindow? firstRun = _firstRunWindow;
        if (firstRun is { IsVisible: true })
            firstRun.IsEnabled = false;
        AppSettings settingsOpenedWith = _settings.Clone();
        var win = new SettingsWindow(_settings, updated =>
        {
            foreach (string profileId in new[] { InputProfile.Profile1Id, InputProfile.Profile2Id })
            {
                InputProfile submittedProfile = updated.GetProfile(profileId);
                submittedProfile.PttMode = ResolvePttModeAfterSettingsSave(
                    _settings.GetProfile(profileId).PttMode,
                    settingsOpenedWith.GetProfile(profileId).PttMode,
                    submittedProfile.PttMode);
            }
            updated.ActiveProfileId = ResolveActiveProfileAfterSettingsSave(
                _settings.ActiveProfileId,
                settingsOpenedWith.ActiveProfileId,
                updated.ActiveProfileId);
            bool profileChanged = updated.ActiveProfileId != _settings.ActiveProfileId;
            _settings = updated;
            _store.Save(_settings);
            ApplyActiveProfile(profileChanged);
            PreverifySelectedLocalModel();
        }, _funAsr, new SettingsWindowActions(
            IsAutoStartEnabled,
            SetAutoStart,
            () => CheckForUpdatesAsync(silent: false),
            PromptAndApplyUpdate,
            () => OpenUri(Log.FilePath),
            InstallFunAsrAsync,
            CancelFunAsrInstall,
            ActiveFunAsrModelId,
            () => _corrections.LoadPairs().Count,
            _corrections.Clear,
            ReviewCorrectionsAsync,
            _updater),
            showLanguageIntelligence);
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

    private Task<UpdateService.CheckResult> CheckForUpdatesAsync(bool silent)
    {
        lock (_updateLock)
        {
            if (_updateCheckTask is { IsCompleted: false })
                return _updateCheckTask;
            _updateCheckTask = CheckForUpdatesCoreAsync(silent);
            return _updateCheckTask;
        }
    }

    private async Task<UpdateService.CheckResult> CheckForUpdatesCoreAsync(bool silent)
    {
        _updater.ReportStatus(new UpdateService.UpdateStatus(
            UpdateService.UpdateStage.Checking,
            "Checking for updates..."));
        var result = await _updater.CheckAsync();
        lock (_updateLock)
        {
            if (result.Outcome == UpdateService.CheckOutcome.UpdateAvailable
                && result.LatestTag is { } tag)
            {
                _availableUpdate = new UpdateCandidate(tag, result.AssetApiUrl, result.AssetSha256);
            }
            else if (result.Outcome == UpdateService.CheckOutcome.UpToDate)
            {
                _availableUpdate = null;
            }
        }
        _updater.ReportStatus(result.Outcome switch
        {
            UpdateService.CheckOutcome.UpdateAvailable => new UpdateService.UpdateStatus(
                UpdateService.UpdateStage.Available,
                $"{result.LatestTag} is available.",
                result.LatestTag),
            UpdateService.CheckOutcome.UpToDate => new UpdateService.UpdateStatus(
                UpdateService.UpdateStage.UpToDate,
                $"You're using the latest version (v{UpdateService.CurrentVersion}).",
                result.LatestTag),
            _ => new UpdateService.UpdateStatus(
                UpdateService.UpdateStage.Failed,
                "Update check failed. Please try again."),
        });
        await _ui.InvokeAsync(() =>
        {
            switch (result.Outcome)
            {
                case UpdateService.CheckOutcome.UpdateAvailable:
                    if (silent)
                        Notify("Update available", $"{result.LatestTag} is available. Tray menu → Update to {result.LatestTag}…");
                    break;
                case UpdateService.CheckOutcome.UpToDate:
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
        UpdateCandidate? update;
        lock (_updateLock)
        {
            if (_updateApplyTask is { IsCompleted: false })
                return;
            update = _availableUpdate is { } candidate
                && string.Equals(candidate.Tag, tag, StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : null;
        }

        if (update?.AssetUrl is null ||
            !UpdateService.UsesPinnedPublisherVerification && update.AssetSha256 is null)
        {
            string message = $"{tag} has no verifiable VoiceInput.exe asset.";
            _updater.ReportFailure(message);
            Notify("Update unavailable", message);
            return;
        }

        var answer = MessageBox.Show(
            $"Update to {tag} now?\n\ngujiguji will download the new version and restart.",
            "gujiguji update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Notify("Updating…", $"Downloading {tag}. The app will restart when it's ready.");
        lock (_updateLock)
        {
            if (_updateApplyTask is { IsCompleted: false })
                return;
            _updateApplyTask = ApplyUpdateAsync(update);
        }
    }

    private async Task ApplyUpdateAsync(UpdateCandidate update)
    {
        bool ok = update.AssetUrl is not null && await _updater.DownloadAndApplyAsync(
            update.Tag,
            update.AssetUrl,
            update.AssetSha256);
        if (ok)
        {
            // Let the Settings window render the restart state before the detached helper takes over.
            await _ui.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(750);
            await _ui.InvokeAsync(Application.Current.Shutdown);
            return;
        }

        // DownloadAndApplyAsync has already published the retryable failure. Release the
        // single-flight guard before the UI can dispatch a click on that newly enabled button.
        lock (_updateLock)
            _updateApplyTask = null;
        await _ui.InvokeAsync(() => Notify(
            "Update failed",
            $"Couldn't download {update.Tag}. Check your connection and try again."));
    }

    // ---------------- Learn from edits ----------------

    private async Task<CorrectionLearningReview> ReviewCorrectionsAsync(AppSettings settings)
    {
        if (!LlmRefiner.HasConnection(settings))
            throw new InvalidOperationException("Set up the language model connection first.");

        var pairs = _corrections.LoadPairs().TakeLast(100).ToList();
        if (pairs.Count < 3)
            throw new InvalidOperationException(
                $"Not enough edits yet ({pairs.Count} captured). At least 3 are required.");

        Log.Write($"Correction learning requested sampleCount={pairs.Count}.");
        Task<string> rules = _refiner.SummarizeCorrectionsAsync(pairs, settings);
        Task<string[]> vocabulary = _refiner.ExtractVocabularyAsync(pairs, settings);
        await Task.WhenAll(rules, vocabulary);
        Log.Write($"Correction learning completed ruleLength={rules.Result.Length} candidateCount={vocabulary.Result.Length}.");
        return new CorrectionLearningReview(rules.Result, vocabulary.Result);
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
