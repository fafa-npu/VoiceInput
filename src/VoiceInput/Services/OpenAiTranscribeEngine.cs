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
    private const int EnergyWindowSamples = AudioCapture.TargetSampleRate / 50;
    private const int MinimumAudibleAmplitude = 131; // 0.004 of full scale, matching the UI silence threshold.
    private const string Scope = "https://cognitiveservices.azure.com/.default";
    // ponytail: bump if Foundry rejects gpt-4o-transcribe at this version.
    private const string ApiVersion = "2025-03-01-preview";
    private const int HttpTimeoutSec = 20;   // upper bound on the transcription POST
    private static readonly TimeSpan TokenRefreshSkew = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly string _url;
    private readonly Func<HttpRequestMessage, CancellationToken, Task> _applyAuth;
    private readonly Func<CancellationToken, Task<AccessToken?>>? _preauthenticate;
    private readonly Func<CancellationToken, Task<AccessToken>>? _getTokenSilently;
    private readonly IReadOnlyList<string> _vocabularyEntries;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly MemoryStream _buffer = new();
    private readonly object _lock = new();
    private string _language = string.Empty;
    private bool _closed;   // guarded by _lock so Feed and the final PCM snapshot have one boundary
    private volatile bool _canceled;   // set on abort/chord-cancel: discard, skip the network call
    private CancellationTokenSource? _requestCts;
    private CancellationTokenSource? _preauthenticationCts;
    private Task<AccessToken?>? _preauthenticationTask;

    internal OpenAiTranscribeEngine(
        string url,
        Func<HttpRequestMessage, CancellationToken, Task> applyAuth,
        IReadOnlyList<string>? vocabularyEntries = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<CancellationToken, Task>? preauthenticate = null,
        Func<CancellationToken, Task<AccessToken?>>? preauthenticateAndGetToken = null,
        Func<CancellationToken, Task<AccessToken>>? getTokenSilently = null)
    {
        _url = url;
        _applyAuth = applyAuth;
        _preauthenticate = preauthenticateAndGetToken ?? (preauthenticate is null
            ? null
            : async cancellationToken =>
            {
                await preauthenticate(cancellationToken).ConfigureAwait(false);
                return (AccessToken?)null;
            });
        _getTokenSilently = getTokenSilently;
        _vocabularyEntries = vocabularyEntries ?? Array.Empty<string>();
        _send = send ?? ((request, cancellationToken) => Http.SendAsync(request, cancellationToken));
    }

    /// <summary>Microsoft Entra ID auth (Bearer token). The SDK acquires and caches the token via the credential.</summary>
    public static OpenAiTranscribeEngine ForEntra(
        string endpoint,
        string deployment,
        TokenCredential credential,
        IReadOnlyList<string>? vocabularyEntries = null)
    {
        Func<CancellationToken, Task<AccessToken>>? getTokenSilently =
            credential is SingleFlightTokenCredential coordinated
                ? async ct => await coordinated.GetTokenSilentlyAsync(
                    new TokenRequestContext(new[] { Scope }),
                    ct)
                : null;
        return new(BuildUrl(endpoint, deployment), async (req, ct) =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }, vocabularyEntries, preauthenticateAndGetToken: async ct =>
        {
            return await credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);
        }, getTokenSilently: getTokenSilently);
    }

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

    // A rare Conditional Access challenge may require the user to complete WAM authentication.
    // Keep this recording alive long enough to finish that one prompt and submit the same audio.
    public int StopTimeoutMs => 150000;

#pragma warning disable CS0067 // batch engine emits no interim hypotheses
    public event Action<string>? Partial;
#pragma warning restore CS0067
    public event Action<string>? Final;
    public event Action<SpeechFault>? Fault;

    public Task StartAsync(string language)
    {
        DisposePreauthentication(cancel: true);
        lock (_lock)
        {
            _closed = false;
            _canceled = false;
            _language = TwoLetter(language);
            _buffer.SetLength(0);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the Entra token check while PCM is already being buffered. If WAM interaction is
    /// required it appears near the start of dictation, and <see cref="StopAsync"/> awaits this
    /// exact attempt instead of opening a second dialog.
    /// </summary>
    internal void BeginAuthentication()
    {
        if (_preauthenticate is null) return;
        lock (_lock)
        {
            if (_closed || _canceled || _preauthenticationTask is not null) return;
            _preauthenticationCts = new CancellationTokenSource();
            _preauthenticationTask = PreauthenticateCoreAsync(_preauthenticationCts.Token);
        }
    }

    private async Task<AccessToken?> PreauthenticateCoreAsync(CancellationToken cancellationToken)
    {
        // Avoid entering Azure.Identity while holding _lock in BeginAuthentication.
        await Task.Yield();
        return await _preauthenticate!(cancellationToken).ConfigureAwait(false);
    }

    public void Cancel()
    {
        CancellationTokenSource? requestCts;
        CancellationTokenSource? preauthenticationCts;
        lock (_lock)
        {
            _canceled = true;
            requestCts = _requestCts;
            preauthenticationCts = _preauthenticationCts;
        }
        // Cancellation can run continuations inline. Never invoke it while holding _lock, and
        // cancel preauthentication first so StopAsync cannot clear it after request cancellation.
        try { preauthenticationCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { requestCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Feed(byte[] pcm16kMono)
    {
        lock (_lock)
        {
            if (_closed) return;
            _buffer.Write(pcm16kMono, 0, pcm16kMono.Length);
        }
    }

    public async Task StopAsync()
    {
        byte[] pcm;
        CancellationTokenSource requestCts;
        lock (_lock)
        {
            // Linearize the final audio boundary: a Feed either completes before this snapshot or
            // observes _closed and is ignored. No callback can append after the submitted snapshot.
            _closed = true;
            pcm = _buffer.ToArray();
            if (_canceled) return;   // aborted/chord-cancelled: discard without a network call
            requestCts = new CancellationTokenSource();
            _requestCts = requestCts;
        }

        try
        {
            // If WAM appeared at PTT start, releasing the key while selecting an account must not
            // cancel that exact sign-in. Finish it even when the captured audio is short or silent;
            // only an explicit Escape/chord cancellation aborts authentication.
            AccessToken? preauthenticatedToken = null;
            bool usedSilentTokenLookup = false;
            Task<AccessToken?>? preauthentication;
            lock (_lock) { preauthentication = _preauthenticationTask; }
            if (preauthentication is not null)
            {
                try
                {
                    preauthenticatedToken = await preauthentication.WaitAsync(requestCts.Token);
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested || _canceled)
                {
                    throw;
                }
                catch (Exception authEx)
                {
                    // WAM can occasionally report a teardown error after it already committed the
                    // token to MSAL's cache. One strict silent lookup can preserve this recording;
                    // it never starts another interactive flight or opens a second dialog.
                    if (_getTokenSilently is null)
                    {
                        ReportAuthenticationFailure(authEx);
                        return;
                    }
                    try
                    {
                        preauthenticatedToken = await _getTokenSilently(requestCts.Token);
                        usedSilentTokenLookup = true;
                        Log.Write("OpenAiTranscribeEngine recovered the completed sign-in from the silent token cache.");
                    }
                    catch (OperationCanceledException) when (requestCts.IsCancellationRequested || _canceled)
                    {
                        throw;
                    }
                    catch
                    {
                        ReportAuthenticationFailure(authEx);
                        return;
                    }
                }
            }

            requestCts.Token.ThrowIfCancellationRequested();
            if (pcm.Length < MinimumPcmBytes)
            {
                Fault?.Invoke(new(
                    SpeechFaultKind.Unknown,
                    "Recording was too short to transcribe. Speak for at least half a second and try again."));
                return;
            }
            if (!HasAudibleSignal(pcm))
            {
                Log.Write("OpenAiTranscribeEngine: silent recording skipped before transcription.");
                return;
            }

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

            // Reuse the exact AccessToken returned by the early WAM attempt. A long recording can
            // cross its expiry, so refresh it only through the strict silent cache path. Never call
            // the interactive credential a second time for the same recording.
            try
            {
                if (preauthenticatedToken is { } token)
                {
                    if (NeedsTokenRefresh(token))
                    {
                        if (_getTokenSilently is null || usedSilentTokenLookup)
                            throw new InvalidOperationException(
                                "The cached Microsoft Entra access token is expired or too close to expiry.");

                        token = await _getTokenSilently(requestCts.Token);
                        usedSilentTokenLookup = true;
                        if (NeedsTokenRefresh(token))
                            throw new InvalidOperationException(
                                "The silently refreshed Microsoft Entra access token is too close to expiry.");
                        Log.Write("OpenAiTranscribeEngine silently refreshed an expiring access token.");
                    }
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                }
                else
                    await _applyAuth(req, requestCts.Token);
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception authEx)
            {
                ReportAuthenticationFailure(authEx);
                return;
            }

            Log.Write($"Vocabulary gpt-request mode=Prompt termCount={_vocabularyEntries.Count} promptIncluded={promptIncluded}");
            using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token);
            httpCts.CancelAfter(TimeSpan.FromSeconds(HttpTimeoutSec));
            using var resp = await _send(req, httpCts.Token);
            string json = await resp.Content.ReadAsStringAsync(httpCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = FormatHttpFailure(resp, json);
                Log.Write($"OpenAiTranscribeEngine HTTP {detail}");
                Fault?.Invoke(resp.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => new(
                        SpeechFaultKind.Authentication,
                        "Azure rejected transcription authentication. Check the API key or switch Azure account in Settings; an administrator may also need to grant resource access.",
                        detail),
                    System.Net.HttpStatusCode.TooManyRequests => new(
                        SpeechFaultKind.Quota,
                        "Transcription is rate-limited or out of quota.",
                        detail),
                    _ => new(
                        SpeechFaultKind.Service,
                        $"Transcription service returned HTTP {(int)resp.StatusCode}.",
                        detail),
                });
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
            DisposePreauthentication(cancel: false);
            lock (_lock)
            {
                if (ReferenceEquals(_requestCts, requestCts))
                    _requestCts = null;
            }
            requestCts.Dispose();
        }
    }

    private void ReportAuthenticationFailure(Exception exception)
    {
        Log.Write($"OpenAiTranscribeEngine token acquisition failed ({exception.GetType().Name}).");
        Fault?.Invoke(new(
            SpeechFaultKind.Authentication,
            "Microsoft Entra sign-in did not complete. Try again or switch the Azure account in Settings.",
            exception.Message));
    }

    public void Dispose()
    {
        lock (_lock) { _closed = true; }
        DisposePreauthentication(cancel: true);
        lock (_lock) { _buffer.Dispose(); }
    }

    private void DisposePreauthentication(bool cancel)
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_lock)
        {
            cancellation = _preauthenticationCts;
            task = _preauthenticationTask;
            _preauthenticationCts = null;
            _preauthenticationTask = null;
        }

        if (cancel)
        {
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (task is not null)
        {
            if (task.IsCompleted)
            {
                if (task.IsFaulted) _ = task.Exception;
                cancellation?.Dispose();
            }
            else
            {
                _ = task.ContinueWith(
                    static (completed, state) =>
                    {
                        if (completed.IsFaulted) _ = completed.Exception;
                        ((CancellationTokenSource?)state)?.Dispose();
                    },
                    cancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        else
        {
            cancellation?.Dispose();
        }
    }

    private static string TwoLetter(string lang) =>
        string.IsNullOrEmpty(lang) ? string.Empty : lang.Split('-')[0].ToLowerInvariant();

    private static bool NeedsTokenRefresh(AccessToken token) =>
        token.ExpiresOn <= DateTimeOffset.UtcNow + TokenRefreshSkew;

    private static bool HasAudibleSignal(byte[] pcm)
    {
        const long minimumEnergy = (long)MinimumAudibleAmplitude * MinimumAudibleAmplitude;
        for (int start = 0; start + 1 < pcm.Length; start += EnergyWindowSamples * 2)
        {
            int end = Math.Min(start + EnergyWindowSamples * 2, pcm.Length);
            long energy = 0;
            int samples = 0;
            for (int offset = start; offset + 1 < end; offset += 2)
            {
                short sample = (short)(pcm[offset] | (pcm[offset + 1] << 8));
                energy += (long)sample * sample;
                samples++;
            }
            if (energy >= minimumEnergy * samples)
                return true;
        }
        return false;
    }

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
