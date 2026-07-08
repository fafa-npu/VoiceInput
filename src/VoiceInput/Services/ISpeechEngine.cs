namespace VoiceInput.Services;

/// <summary>A streaming speech-to-text backend.</summary>
public interface ISpeechEngine : IDisposable
{
    /// <summary>True if the engine consumes PCM via <see cref="Feed"/> (Azure). False if it opens its own mic (Windows).</summary>
    bool NeedsAudioFeed { get; }

    /// <summary>Upper bound the controller allows <see cref="StopAsync"/> to run before force-disposing.
    /// Streaming engines stop fast; a batch engine that transcribes on stop overrides this higher.</summary>
    int StopTimeoutMs => 2500;

    /// <summary>True if the engine streams interim hypotheses while you talk. False for batch engines
    /// (e.g. gpt-4o-transcribe) that only produce text after <see cref="StopAsync"/>.</summary>
    bool HasInterimResults => true;

    /// <summary>Interim hypothesis for the current segment.</summary>
    event Action<string>? Partial;

    /// <summary>A finalized segment of recognized text.</summary>
    event Action<string>? Final;

    Task StartAsync(string language);
    void Feed(byte[] pcm16kMono);
    Task StopAsync();

    /// <summary>Abort the current session: discard buffered audio and do NOT finalize/transcribe.
    /// Called on chord-cancel and error teardown so a discarded session never does pointless work
    /// (e.g. a network transcription whose result is thrown away). Default: no-op.</summary>
    void Cancel() { }
}

/// <summary>Thrown when the requested recognition language is not installed/available on the device.</summary>
public sealed class SpeechLanguageUnavailableException(string language)
    : Exception($"Speech recognition language '{language}' is not installed.")
{
    public string Language { get; } = language;
}
