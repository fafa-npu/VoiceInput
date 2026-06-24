using System.Linq;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace VoiceInput.Services;

/// <summary>
/// On-device dictation via Windows.Media.SpeechRecognition. Opens its own microphone, so the
/// app's WASAPI capture is used only for the waveform meter here (<see cref="NeedsAudioFeed"/> = false).
/// Note: the predefined dictation grammar is web-service-backed, so it may require the user's
/// "Online speech recognition" privacy setting to be on, and the language pack to be installed.
/// </summary>
public sealed class WindowsSpeechEngine : ISpeechEngine
{
    public bool NeedsAudioFeed => false;
    public event Action<string>? Partial;
    public event Action<string>? Final;

    private SpeechRecognizer? _rec;

    /// <summary>Diagnostic: the dictation-capable languages installed on this device.</summary>
    public static string SupportedTopicLanguageTags()
    {
        try
        {
            var tags = SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToArray();
            return tags.Length == 0 ? "(none)" : string.Join(", ", tags);
        }
        catch (Exception ex)
        {
            return "(query failed: " + ex.Message + ")";
        }
    }

    /// <summary>
    /// Maps a requested tag (e.g. "zh-CN") to the actual installed dictation tag
    /// (e.g. "zh-Hans-CN"), since Windows reports script-qualified tags. Falls back to a
    /// primary-subtag + region match, then primary-subtag only. Returns null if none installed.
    /// </summary>
    public static string? ResolveInstalledTag(string requested)
    {
        try
        {
            var installed = SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToList();
            var exact = installed.FirstOrDefault(t => t.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var parts = requested.Split('-');
            string primary = parts[0];
            string? region = parts.Length > 1 ? parts[^1] : null;

            if (region is not null)
            {
                var byRegion = installed.FirstOrDefault(t =>
                {
                    var p = t.Split('-');
                    return p[0].Equals(primary, StringComparison.OrdinalIgnoreCase)
                        && p[^1].Equals(region, StringComparison.OrdinalIgnoreCase);
                });
                if (byRegion is not null) return byRegion;
            }

            return installed.FirstOrDefault(t =>
                t.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public static bool IsLanguageSupported(string language) => ResolveInstalledTag(language) is not null;

    public async Task StartAsync(string language)
    {
        string? tag = ResolveInstalledTag(language);
        if (tag is null)
            throw new SpeechLanguageUnavailableException(language);
        Log.Write($"WindowsSpeechEngine: requested '{language}' -> using installed '{tag}'");

        _rec = new SpeechRecognizer(new Language(tag));
        _rec.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));

        var compile = await _rec.CompileConstraintsAsync();
        if (compile.Status != SpeechRecognitionResultStatus.Success)
        {
            _rec.Dispose();
            _rec = null;
            throw new SpeechLanguageUnavailableException(language);
        }

        _rec.HypothesisGenerated += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Hypothesis.Text))
                Partial?.Invoke(e.Hypothesis.Text);
        };
        _rec.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
        {
            if (e.Result.Status == SpeechRecognitionResultStatus.Success && !string.IsNullOrEmpty(e.Result.Text))
                Final?.Invoke(e.Result.Text);
        };

        await _rec.ContinuousRecognitionSession.StartAsync();
    }

    public void Feed(byte[] pcm16kMono) { /* uses its own mic */ }

    public async Task StopAsync()
    {
        if (_rec is null) return;
        try { await _rec.ContinuousRecognitionSession.StopAsync(); }
        catch { /* already stopped */ }
    }

    public void Dispose()
    {
        try { _rec?.Dispose(); } catch { }
        _rec = null;
    }
}
