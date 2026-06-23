using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VoiceInput.Models;

namespace VoiceInput.Services;

/// <summary>
/// Optional LLM post-processing of the transcript via an OpenAI-compatible Chat Completions API.
/// The system prompt is deliberately conservative: fix only obvious speech-recognition errors,
/// never rewrite or polish. Fails open — on any error the original transcript is returned.
/// </summary>
public sealed class LlmRefiner
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string SystemPrompt =
        "You correct raw speech-to-text output for an input method. " +
        "The text may be Chinese, English, or a mix, and usually has no punctuation.\n" +
        "CRITICAL RULE - LANGUAGE IS PRESERVED: Never translate. The output MUST be in the exact same " +
        "language(s) as the input. English input stays English. Chinese input stays Chinese. A mix stays " +
        "the same mix. Translating between languages is a critical failure.\n" +
        "Do these:\n" +
        "1. Fix obvious recognition errors: Chinese homophones; technical terms misheard into Chinese " +
        "phonetics (配森 -> Python, 杰森 -> JSON, 瑞克特 -> React).\n" +
        "2. Remove filler words and verbal hesitations: Chinese 嗯/呃/额/啊/哦/唉 and filler uses of " +
        "那个/这个/就是/然后; English um/uh/er/ah and filler uses of like / you know / I mean. " +
        "Remove them ONLY when they are fillers; keep them when they carry meaning " +
        "(e.g. 那个 meaning \"that\", \"like\" meaning \"similar to\").\n" +
        "3. Add natural punctuation and sentence breaks. Use full-width punctuation (，。？！、：) for " +
        "Chinese text and ASCII punctuation for English text; capitalize English sentence starts and the word \"I\".\n" +
        "Otherwise do NOT rewrite, rephrase, reorder, summarize, or change any wording; keep the meaning exactly.\n" +
        "Output ONLY the corrected text, nothing else.";

    /// <summary>Refine <paramref name="text"/>; returns the original on any failure.</summary>
    public async Task<string> RefineAsync(string text, AppSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try
        {
            string content = await CallAsync(settings, text, ct);
            return string.IsNullOrWhiteSpace(content) ? text : content.Trim();
        }
        catch
        {
            return text;   // fail open: never block injection on an LLM error
        }
    }

    /// <summary>Used by the Settings "Test" button.</summary>
    public async Task<(bool ok, string message)> TestAsync(AppSettings settings, CancellationToken ct = default)
    {
        try
        {
            string content = await CallAsync(settings, "测试 配森 和 杰森", ct);
            return (true, "OK — model replied: " + content.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<string> CallAsync(AppSettings settings, string userText, CancellationToken ct)
    {
        string url = settings.LlmBaseUrl.TrimEnd('/') + "/chat/completions";

        var payload = new
        {
            model = settings.LlmModel,
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userText },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(settings.LlmApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.LlmApiKey);

        using var resp = await Http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Truncate(json, 300)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
