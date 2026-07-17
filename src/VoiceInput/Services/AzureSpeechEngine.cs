using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace VoiceInput.Services;

internal readonly record struct AzureVocabularyApplyResult(int AppliedCount, string? ExceptionType);

/// <summary>
/// Streaming recognition via the Azure Speech SDK. Consumes 16 kHz/16-bit/mono PCM fed from the
/// app's WASAPI capture through a push stream (<see cref="NeedsAudioFeed"/> = true).
/// Supports both account-key (local auth) and Microsoft Entra ID authentication.
/// </summary>
public sealed class AzureSpeechEngine : ISpeechEngine
{
    internal const int MaxVocabularyPhrases = 500;

    private readonly Func<SpeechConfig> _configFactory;
    private readonly IReadOnlyList<string> _vocabularyEntries;

    private AzureSpeechEngine(
        Func<SpeechConfig> configFactory,
        IReadOnlyList<string>? vocabularyEntries = null)
    {
        _configFactory = configFactory;
        _vocabularyEntries = vocabularyEntries ?? Array.Empty<string>();
    }

    /// <summary>Account-key (local auth) engine.</summary>
    public static AzureSpeechEngine ForKey(
        string subscriptionKey,
        string region,
        IReadOnlyList<string>? vocabularyEntries = null) =>
        new(() => SpeechConfig.FromSubscription(subscriptionKey, region), vocabularyEntries);

    /// <summary>Microsoft Entra ID engine. <paramref name="endpoint"/> is the resource's custom-domain
    /// endpoint; the SDK acquires and auto-refreshes tokens via <paramref name="credential"/>.</summary>
    public static AzureSpeechEngine ForEntra(
        string endpoint,
        TokenCredential credential,
        IReadOnlyList<string>? vocabularyEntries = null) =>
        new(() => SpeechConfig.FromEndpoint(new Uri(endpoint), credential), vocabularyEntries);

    public bool NeedsAudioFeed => true;
    public event Action<string>? Partial;
    public event Action<string>? Final;
    public event Action<SpeechFault>? Fault;

    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _push;
    private AudioConfig? _audioConfig;
    private volatile bool _closed;   // set before teardown so Feed stops writing the push stream

    public async Task StartAsync(string language)
    {
        var config = _configFactory();
        config.SpeechRecognitionLanguage = language;

        _push = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(AudioCapture.TargetSampleRate, 16, 1));
        _audioConfig = AudioConfig.FromStreamInput(_push);
        _recognizer = new SpeechRecognizer(config, _audioConfig);

        if (_vocabularyEntries.Count > 0)
        {
            AzureVocabularyApplyResult result;
            try
            {
                PhraseListGrammar grammar = PhraseListGrammar.FromRecognizer(_recognizer);
                result = AddVocabularyPhrases(_vocabularyEntries, grammar.AddPhrase);
            }
            catch (Exception ex)
            {
                result = new(0, ex.GetType().Name);
            }

            Log.Write(FormatVocabularyLog(_vocabularyEntries.Count, result));
        }

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
            {
                Log.Write($"AzureSpeechEngine canceled: {e.ErrorCode} - {e.ErrorDetails}");
                Fault?.Invoke(MapFault(e.ErrorCode, e.ErrorDetails));
            }
        };

        await _recognizer.StartContinuousRecognitionAsync();
    }

    internal static AzureVocabularyApplyResult AddVocabularyPhrases(
        IReadOnlyList<string> entries,
        Action<string> addPhrase)
    {
        int applied = 0;
        try
        {
            int count = Math.Min(entries.Count, MaxVocabularyPhrases);
            for (int index = 0; index < count; index++)
            {
                addPhrase(entries[index]);
                applied++;
            }
            return new(applied, null);
        }
        catch (Exception ex)
        {
            return new(applied, ex.GetType().Name);
        }
    }

    internal static string FormatVocabularyLog(int requested, AzureVocabularyApplyResult result) =>
        result.ExceptionType is not null
            ? $"WARN Vocabulary azure-phrase-list requested={requested} applied={result.AppliedCount} exceptionType={result.ExceptionType}"
            : requested > MaxVocabularyPhrases
                ? $"Vocabulary azure-phrase-list requested={requested} applied={result.AppliedCount} limit={MaxVocabularyPhrases}"
                : $"Vocabulary azure-phrase-list requested={requested} applied={result.AppliedCount}";

    private static SpeechFault MapFault(CancellationErrorCode code, string detail) => code switch
    {
        CancellationErrorCode.AuthenticationFailure => new(SpeechFaultKind.Authentication,
            "Azure Speech authentication failed. Check the selected account, key, and resource settings.", detail),
        CancellationErrorCode.TooManyRequests => new(SpeechFaultKind.Quota,
            "Azure Speech is rate-limited or out of quota. Try again later or check the resource quota.", detail),
        CancellationErrorCode.ConnectionFailure or CancellationErrorCode.ServiceTimeout => new(SpeechFaultKind.Network,
            "Azure Speech could not be reached. Check your network and try again.", detail),
        _ => new(SpeechFaultKind.Service, "Azure Speech could not transcribe this recording.", detail),
    };

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
