namespace VoiceInput.Services;

/// <summary>A streaming speech-to-text backend.</summary>
public interface ISpeechEngine : IDisposable
{
    /// <summary>True if the engine consumes PCM via <see cref="Feed"/> (Azure). False if it opens its own mic (Windows).</summary>
    bool NeedsAudioFeed { get; }

    /// <summary>Interim hypothesis for the current segment.</summary>
    event Action<string>? Partial;

    /// <summary>A finalized segment of recognized text.</summary>
    event Action<string>? Final;

    Task StartAsync(string language);
    void Feed(byte[] pcm16kMono);
    Task StopAsync();
}

/// <summary>Thrown when the requested recognition language is not installed/available on the device.</summary>
public sealed class SpeechLanguageUnavailableException(string language)
    : Exception($"Speech recognition language '{language}' is not installed.")
{
    public string Language { get; } = language;
}
