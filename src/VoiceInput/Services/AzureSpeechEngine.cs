using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace VoiceInput.Services;

/// <summary>
/// Streaming recognition via the Azure Speech SDK. Consumes 16 kHz/16-bit/mono PCM fed from the
/// app's WASAPI capture through a push stream (<see cref="NeedsAudioFeed"/> = true).
/// </summary>
public sealed class AzureSpeechEngine(string subscriptionKey, string region) : ISpeechEngine
{
    public bool NeedsAudioFeed => true;
    public event Action<string>? Partial;
    public event Action<string>? Final;

    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _push;
    private AudioConfig? _audioConfig;
    private volatile bool _closed;   // set before teardown so Feed stops writing the push stream

    public async Task StartAsync(string language)
    {
        var config = SpeechConfig.FromSubscription(subscriptionKey, region);
        config.SpeechRecognitionLanguage = language;

        _push = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(AudioCapture.TargetSampleRate, 16, 1));
        _audioConfig = AudioConfig.FromStreamInput(_push);
        _recognizer = new SpeechRecognizer(config, _audioConfig);

        _recognizer.Recognizing += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Result.Text))
                Partial?.Invoke(e.Result.Text);
        };
        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
                Final?.Invoke(e.Result.Text);
        };
        // Auth failures, quota, and network drops surface here (not as a StartAsync exception).
        _recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                Log.Write($"AzureSpeechEngine canceled: {e.ErrorCode} - {e.ErrorDetails}");
        };

        await _recognizer.StartContinuousRecognitionAsync();
    }

    public void Feed(byte[] pcm16kMono)
    {
        if (_closed) return;
        _push?.Write(pcm16kMono);
    }

    public async Task StopAsync()
    {
        _closed = true;
        try
        {
            _push?.Close();
            if (_recognizer is not null)
                await _recognizer.StopContinuousRecognitionAsync();
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _closed = true;
        try { _recognizer?.Dispose(); } catch { }
        try { _audioConfig?.Dispose(); } catch { }
        try { _push?.Dispose(); } catch { }
        _recognizer = null;
        _audioConfig = null;
        _push = null;
    }
}
