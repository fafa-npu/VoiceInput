using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

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
    private const string Scope = "https://cognitiveservices.azure.com/.default";
    // ponytail: bump if Foundry rejects gpt-4o-transcribe at this version.
    private const string ApiVersion = "2025-03-01-preview";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly string _url;
    private readonly Func<HttpRequestMessage, CancellationToken, Task> _applyAuth;
    private readonly MemoryStream _buffer = new();
    private readonly object _lock = new();
    private string _language = string.Empty;
    private volatile bool _closed;

    private OpenAiTranscribeEngine(string url, Func<HttpRequestMessage, CancellationToken, Task> applyAuth)
    {
        _url = url;
        _applyAuth = applyAuth;
    }

    /// <summary>Microsoft Entra ID auth (Bearer token). The SDK acquires and caches the token via the credential.</summary>
    public static OpenAiTranscribeEngine ForEntra(string endpoint, string deployment, TokenCredential credential) =>
        new(BuildUrl(endpoint, deployment), async (req, ct) =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        });

    /// <summary>Account-key auth (the Azure OpenAI <c>api-key</c> header).</summary>
    public static OpenAiTranscribeEngine ForKey(string endpoint, string deployment, string apiKey) =>
        new(BuildUrl(endpoint, deployment), (req, _) =>
        {
            req.Headers.Add("api-key", apiKey);
            return Task.CompletedTask;
        });

    private static string BuildUrl(string endpoint, string deployment) =>
        endpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/audio/transcriptions?api-version=" + ApiVersion;

    public bool NeedsAudioFeed => true;

    public bool HasInterimResults => false;

    // Batch transcription runs inside StopAsync. Allow generous time for the FIRST interactive
    // Entra sign-in (browser popup); after that the token is cached and acquisition is instant.
    public int StopTimeoutMs => 120000;

#pragma warning disable CS0067 // batch engine emits no interim hypotheses
    public event Action<string>? Partial;
#pragma warning restore CS0067
    public event Action<string>? Final;

    public Task StartAsync(string language)
    {
        _closed = false;
        _language = TwoLetter(language);
        lock (_lock) { _buffer.SetLength(0); }
        return Task.CompletedTask;
    }

    public void Feed(byte[] pcm16kMono)
    {
        if (_closed) return;
        lock (_lock) { _buffer.Write(pcm16kMono, 0, pcm16kMono.Length); }
    }

    public async Task StopAsync()
    {
        _closed = true;
        byte[] pcm;
        lock (_lock) { pcm = _buffer.ToArray(); }
        if (pcm.Length == 0) return;

        try
        {
            byte[] wav = BuildWav(pcm, AudioCapture.TargetSampleRate);

            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "audio.wav");
            form.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrEmpty(_language)) form.Add(new StringContent(_language), "language");

            using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = form };
            await _applyAuth(req, CancellationToken.None);

            using var resp = await Http.SendAsync(req, CancellationToken.None);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"OpenAiTranscribeEngine HTTP {(int)resp.StatusCode}: {Truncate(json, 300)}");
                return;
            }

            using var doc = JsonDocument.Parse(json);
            string? text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
                Final?.Invoke(text!.Trim());
        }
        catch (Exception ex)
        {
            Log.Error("OpenAiTranscribeEngine.StopAsync", ex);
        }
    }

    public void Dispose()
    {
        _closed = true;
        _buffer.Dispose();
    }

    private static string TwoLetter(string lang) =>
        string.IsNullOrEmpty(lang) ? string.Empty : lang.Split('-')[0].ToLowerInvariant();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Wrap raw 16-bit mono PCM in a minimal 44-byte WAV header.</summary>
    private static byte[] BuildWav(byte[] pcm, int sampleRate)
    {
        const int bits = 16, channels = 1;
        int byteRate = sampleRate * channels * bits / 8;
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                       // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bits / 8));   // block align
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
