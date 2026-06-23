using System.Diagnostics;
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

    private OverlayWindow? _overlay;
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;

    private ISpeechEngine? _engine;
    private readonly StringBuilder _finals = new();
    private string _partial = string.Empty;
    private volatile float _level;
    private bool _dictating;
    // Serializes the start/stop/cancel lifecycle so sessions can never overlap
    // (Windows dictation forbids overlapping audio-engine sessions).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppController()
    {
        _ui = Application.Current.Dispatcher;
        _settings = _store.Load();

        _hook = new KeyboardHook(_settings.PttKey);
        _hook.Engaged += () => _ui.BeginInvoke(() => _ = StartDictationAsync());
        _hook.Released += () => _ui.BeginInvoke(() => _ = StopDictationAsync());
        _hook.Cancelled += () => _ui.BeginInvoke(() => _ = CancelDictationAsync());

        _audio.LevelChanged += lvl => _level = lvl;
    }

    public void Start()
    {
        _overlay = new OverlayWindow { LevelSource = () => _level };
        BuildTray();
        _hook.Install();
        Log.Write($"=== VoiceInput started. ptt={_settings.PttKey}, engine={_settings.Engine}, lang={_settings.Language}, llm={_settings.LlmEnabled} ===");
        Log.Write("Windows dictation languages available: " + WindowsSpeechEngine.SupportedTopicLanguageTags());
    }

    // ---------------- Dictation flow ----------------

    private async Task StartDictationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_dictating) return;
            DisposeEngine();           // safety: clear any engine a prior session failed to tear down
            _dictating = true;
            _finals.Clear();
            _partial = string.Empty;
            _level = 0;

            _overlay!.ShowListening(Placeholder());
            _audio.Start();

            _engine = CreateEngine(out bool azureFellBack);
            if (azureFellBack)
                Notify("Azure key not set", "Falling back to Windows on-device recognition.");

            Log.Write($"PTT engaged -> start dictation (engine={_engine.GetType().Name}, lang={_settings.Language}, azureFellBack={azureFellBack})");

            _engine.Partial += OnPartial;
            _engine.Final += OnFinal;
            if (_engine.NeedsAudioFeed)
                _audio.PcmChunkAvailable += OnPcm;

            try
            {
                await _engine.StartAsync(_settings.Language);
                Log.Write("Engine started OK, listening…");
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
                Log.Error("StartAsync", ex);
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

            await TeardownEngineAsync();

            string text = ComposeTranscript();
            Log.Write($"PTT released -> transcript ({text.Length} chars): \"{Trim(text)}\"");

            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Write("Empty transcript — nothing recognized. Check mic input / language pack.");
                _overlay!.HideAnimated();
                return;
            }

            if (_settings.LlmEnabled && !string.IsNullOrWhiteSpace(_settings.LlmBaseUrl))
            {
                _overlay!.SetStatus("Refining…");
                string refined = await _refiner.RefineAsync(text, _settings);
                Log.Write($"LLM refine: \"{Trim(text)}\" -> \"{Trim(refined)}\"");
                text = refined;
            }

            _overlay!.HideAnimated();
            await _injector.InjectAsync(text);
            Log.Write("Injected into focused control.");
        }
        finally
        {
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
        await TeardownEngineAsync();
        _finals.Clear();
        _partial = string.Empty;
        _overlay?.HideAnimated();
    }

    /// <summary>
    /// Stops audio + engine, bounding the engine stop with a timeout so a hung
    /// ContinuousRecognitionSession.StopAsync can't wedge the lifecycle, then disposes the engine.
    /// </summary>
    private async Task TeardownEngineAsync()
    {
        _audio.Stop();
        if (_engine is not null)
        {
            if (_engine.NeedsAudioFeed) _audio.PcmChunkAvailable -= OnPcm;
            bool stopped = await TryAwait(_engine.StopAsync(), 2500);
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
        _partial = text;
        _ui.BeginInvoke(() => _overlay?.SetText(ComposeTranscript()));
    }

    private void OnFinal(string segment)
    {
        AppendFinal(segment);
        _partial = string.Empty;
        _ui.BeginInvoke(() => _overlay?.SetText(ComposeTranscript()));
    }

    private void OnPcm(byte[] pcm) => _engine?.Feed(pcm);

    private void AppendFinal(string segment)
    {
        if (_finals.Length > 0 && NeedsSpace())
            _finals.Append(' ');
        _finals.Append(segment);
    }

    private string ComposeTranscript()
    {
        if (_partial.Length == 0) return _finals.ToString();
        if (_finals.Length == 0) return _partial;
        return _finals + (NeedsSpace() ? " " : string.Empty) + _partial;
    }

    private bool NeedsSpace() =>
        !(_settings.Language.StartsWith("zh") || _settings.Language.StartsWith("ja") || _settings.Language.StartsWith("ko"));

    private ISpeechEngine CreateEngine(out bool azureFellBack)
    {
        azureFellBack = false;
        if (_settings.Engine == SpeechEngineKind.Azure)
        {
            if (!string.IsNullOrWhiteSpace(_settings.AzureKey) && !string.IsNullOrWhiteSpace(_settings.AzureRegion))
                return new AzureSpeechEngine(_settings.AzureKey, _settings.AzureRegion);
            azureFellBack = true;
        }
        return new WindowsSpeechEngine();
    }

    private void DisposeEngine()
    {
        if (_engine is null) return;
        _engine.Partial -= OnPartial;
        _engine.Final -= OnFinal;
        _engine.Dispose();
        _engine = null;
    }

    private string Placeholder() => _settings.Language.StartsWith("zh") ? "聆听中…" : "Listening…";

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

    private string TrayTooltip() => $"VoiceInput — hold {PttDisplay(_settings.PttKey)} to talk";

    private static string PttDisplay(string key) => key switch
    {
        "RightCtrl" => "Right Ctrl",
        "CapsLock" => "Caps Lock",
        "RightAlt" => "Right Alt",
        "RightShift" => "Right Shift",
        _ => key,
    };

    private void RebuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

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
        menu.Items.Add(engine);

        // Push-to-talk key
        var ptt = new WinForms.ToolStripMenuItem("Push-to-talk key");
        foreach (var key in new[] { "RightCtrl", "CapsLock", "RightAlt", "RightShift" })
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
        enabled.Click += (_, _) => { _settings.LlmEnabled = !_settings.LlmEnabled; Persist(); RebuildMenu(); };
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

    private void Notify(string title, string message) =>
        _tray?.ShowBalloonTip(6000, title, message, WinForms.ToolTipIcon.Info);

    private static bool IsPrivacyPolicyError(Exception ex) =>
        ex.Message.Contains("privacy", StringComparison.OrdinalIgnoreCase);

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* settings deep-link unavailable */ }
    }

    public void Dispose()
    {
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
