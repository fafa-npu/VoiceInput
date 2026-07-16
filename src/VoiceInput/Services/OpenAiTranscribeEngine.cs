using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using VoiceInput.Models;

namespace VoiceInput.Services;

/// <summary>
/// Batch transcription via an Azure AI Foundry / OpenAI gpt-4o-transcribe deployment.
/// Buffers the captured 16 kHz/16-bit/mono PCM while you talk, then on <see cref="StopAsync"/>
/// (push-to-talk release) wraps it as WAV and POSTs once to the transcriptions endpoint.
/// Supports both account-key (<c>api-key</c> header) and Microsoft Entra ID (Bearer) auth.
/// No interim hypotheses — the text arrives ~0.5–2 s after you stop speaking.
/// </summary>
public sealed class OpenAiTranscribeEngine : ISpeechEngine
{
    // 0.5 s of 16 kHz 16-bit mono PCM; shorter WAVs are rejected by GPT transcription.
    internal const int MinimumPcmBytes = AudioCapture.TargetSampleRate;
    private const string Scope = "https://cognitiveservices.azure.com/.default";
    // ponytail: bump if Foundry rejects gpt-4o-transcribe at this version.
    private const string ApiVersion = "2025-03-01-preview";
    private const int AuthTimeoutSec = 8;    // token must be cached/silent; if expired we bail fast and re-auth off-lock
    private const int HttpTimeoutSec = 20;   // upper bound on the transcription POST
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly string _url;
    private readonly Func<HttpRequestMessage, CancellationToken, Task> _applyAuth;
    private readonly IReadOnlyList<string> _vocabularyEntries;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly MemoryStream _buffer = new();
    private readonly object _lock = new();
    private string _language = string.Empty;
    private volatile bool _closed;
    private volatile bool _canceled;   // set on abort/chord-cancel: discard, skip the network call
    private CancellationTokenSource? _requestCts;

    /// <summary>Raised when acquiring the Entra token fails/times out (expired sign-in) so the app
    /// can notify the user and re-authenticate in the background, off the dictation lock.</summary>
    public event Action? AuthExpired;

    internal OpenAiTranscribeEngine(
        string url,
        Func<HttpRequestMessage, CancellationToken, Task> applyAuth,
        IReadOnlyList<string>? vocabularyEntries = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        _url = url;
        _applyAuth = applyAuth;
        _vocabularyEntries = vocabularyEntries ?? Array.Empty<string>();
        _send = send ?? ((request, cancellationToken) => Http.SendAsync(request, cancellationToken));
    }

    /// <summary>Microsoft Entra ID auth (Bearer token). The SDK acquires and caches the token via the credential.</summary>
    public static OpenAiTranscribeEngine ForEntra(
        string endpoint,
        string deployment,
        TokenCredential credential,
        IReadOnlyList<string>? vocabularyEntries = null) =>
        new(BuildUrl(endpoint, deployment), async (req, ct) =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }, vocabularyEntries);

    /// <summary>Account-key auth (the Azure OpenAI <c>api-key</c> header).</summary>
    public static OpenAiTranscribeEngine ForKey(
        string endpoint,
        string deployment,
        string apiKey,
        IReadOnlyList<string>? vocabularyEntries = null) =>
        new(BuildUrl(endpoint, deployment), (req, _) =>
        {
            req.Headers.Add("api-key", apiKey);
            return Task.CompletedTask;
        }, vocabularyEntries);

    private static string BuildUrl(string endpoint, string deployment) =>
        endpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/audio/transcriptions?api-version=" + ApiVersion;

    public bool NeedsAudioFeed => true;

    public bool HasInterimResults => false;

    // Batch transcription runs inside StopAsync while the dictation lock is held, so it must return
    // promptly. Token acquisition and the HTTP POST are each hard-bounded below; this is the outer
    // ceiling the controller enforces.
    public int StopTimeoutMs => 30000;

#pragma warning disable CS0067 // batch engine emits no interim hypotheses
    public event Action<string>? Partial;
#pragma warning restore CS0067
    public event Action<string>? Final;
    public event Action<SpeechFault>? Fault;

    public Task StartAsync(string language)
    {
        _closed = false;
        _canceled = false;
        _language = TwoLetter(language);
        lock (_lock) { _buffer.SetLength(0); }
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _canceled = true;
            _requestCts?.Cancel();
        }
    }

    public void Feed(byte[] pcm16kMono)
    {
        if (_closed) return;
        lock (_lock) { _buffer.Write(pcm16kMono, 0, pcm16kMono.Length); }
    }

    public async Task StopAsync()
    {
        _closed = true;
        if (_canceled) return;   // aborted / chord-cancelled: transcript is discarded, so skip the network call

        byte[] pcm;
        lock (_lock) { pcm = _buffer.ToArray(); }
        if (pcm.Length < MinimumPcmBytes)
        {
            Fault?.Invoke(new(
                SpeechFaultKind.Unknown,
                "Recording was too short to transcribe. Speak for at least half a second and try again."));
            return;
        }

        var requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(HttpTimeoutSec));
        lock (_lock)
        {
            _requestCts = requestCts;
            if (_canceled) requestCts.Cancel();
        }

        try
        {
            byte[] wav = PcmWave.Wrap(pcm, AudioCapture.TargetSampleRate);

            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "audio.wav");
            form.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrEmpty(_language)) form.Add(new StringContent(_language), "language");
            bool promptIncluded = _vocabularyEntries.Count > 0;
            if (promptIncluded)
                form.Add(new StringContent(RecognitionVocabulary.BuildPrompt(_vocabularyEntries)), "prompt");

            using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = form };

            // Acquire the token with a hard timeout. A valid cached token is instant; if the sign-in
            // has expired the credential would otherwise block on an interactive prompt — bound it so
            // StopAsync (which holds the dictation lock) can never wedge, and hand re-auth to the app.
            try
            {
                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token);
                authCts.CancelAfter(TimeSpan.FromSeconds(AuthTimeoutSec));
                await _applyAuth(req, authCts.Token);
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception authEx)
            {
                Log.Write($"OpenAiTranscribeEngine token acquisition failed ({authEx.GetType().Name}); requesting re-auth.");
                Fault?.Invoke(new(SpeechFaultKind.Authentication,
                    "Azure sign-in failed or expired. Sign in again from Settings.", authEx.Message));
                AuthExpired?.Invoke();
                return;
            }

            Log.Write($"Vocabulary gpt-request mode=Prompt termCount={_vocabularyEntries.Count} promptIncluded={promptIncluded}");
            using var resp = await _send(req, requestCts.Token);
            string json = await resp.Content.ReadAsStringAsync(requestCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = FormatHttpFailure(resp, json);
                Log.Write($"OpenAiTranscribeEngine HTTP {detail}");
                Fault?.Invoke(resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? new(SpeechFaultKind.Quota, "Transcription is rate-limited or out of quota.", detail)
                    : new(SpeechFaultKind.Service, $"Transcription service returned HTTP {(int)resp.StatusCode}.", detail));
                return;
            }

            using var doc = JsonDocument.Parse(json);
            string? text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
                Final?.Invoke(text!.Trim());
        }
        catch (OperationCanceledException)
        {
            if (!_canceled)
            {
                Log.Write("OpenAiTranscribeEngine: transcription timed out.");
                Fault?.Invoke(new(SpeechFaultKind.Timeout, "Transcription timed out. Your recording was not inserted."));
            }
        }
        catch (Exception ex)
        {
            Log.Error("OpenAiTranscribeEngine.StopAsync", ex);
            Fault?.Invoke(new(SpeechFaultKind.Network, "Transcription failed. Check your network and try again.", ex.Message));
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_requestCts, requestCts))
                    _requestCts = null;
            }
            requestCts.Dispose();
        }
    }

    public void Dispose()
    {
        _closed = true;
        _buffer.Dispose();
    }

    private static string TwoLetter(string lang) =>
        string.IsNullOrEmpty(lang) ? string.Empty : lang.Split('-')[0].ToLowerInvariant();

    internal static string FormatHttpFailure(HttpResponseMessage response, string body)
    {
        string detail = $"status={(int)response.StatusCode}";
        string? code = SafeErrorCode(body);
        if (code is not null) detail += $" code={code}";

        string? requestId = SafeHeader(response, "x-request-id")
            ?? SafeHeader(response, "apim-request-id")
            ?? SafeHeader(response, "x-ms-request-id")
            ?? SafeHeader(response, "request-id");
        if (requestId is not null) detail += $" requestId={requestId}";
        return detail;
    }

    private static string? SafeErrorCode(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out JsonElement code) &&
                code.ValueKind == JsonValueKind.String &&
                IsSafeIdentifier(code.GetString()))
            {
                return code.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? SafeHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values)) return null;
        return values.FirstOrDefault(IsSafeIdentifier);
    }

    private static bool IsSafeIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

}
