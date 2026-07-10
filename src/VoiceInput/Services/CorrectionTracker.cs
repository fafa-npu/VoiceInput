using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace VoiceInput.Services;

/// <summary>
/// Captures (raw STT → refined/injected → user-edited) triples: after injecting we "arm" with the
/// raw recognizer output and the injected text; when the user presses Enter (done-editing signal) we
/// read the focused input box back and, if it differs from what we injected, append the triple to
/// corrections.jsonl. Noise is left for the learning step's LLM to filter.
/// ponytail: best-effort capture; if the box already submitted/cleared we just miss that one.
/// </summary>
public sealed class CorrectionTracker
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceInput", "corrections.jsonl");

    private string? _raw;        // raw recognizer output (pre-refine)
    private string? _injected;   // what we actually typed into the box (refined, or raw if no LLM)
    private TextInjector.Target? _target;
    private DateTimeOffset _expiresAt;
    private const int MaxEntries = 100;
    private const int MaxTextLength = 1000;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoiceInput.corrections.v2");

    public void Arm(string raw, string injected, TextInjector.Target target)
    {
        _raw = Limit(raw);
        _injected = Limit(injected);
        _target = target;
        _expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
    }

    public async Task CaptureAsync(ContextReader reader, TextInjector injector)
    {
        var raw = _raw;
        var injected = _injected;
        var target = _target;
        _raw = _injected = null;   // one-shot
        _target = null;
        if (string.IsNullOrWhiteSpace(injected) || target is null ||
            DateTimeOffset.UtcNow > _expiresAt || !injector.IsCurrentTarget(target)) return;

        string? edited = Limit((await reader.TryReadFocusedValueAsync())?.Trim());
        if (string.IsNullOrWhiteSpace(edited) || edited == injected) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var pairs = LoadPairs();
            pairs.Add((raw ?? string.Empty, injected, edited));
            WritePairs(pairs.TakeLast(MaxEntries));
            Log.Write($"correction captured (raw {raw?.Length ?? 0}, injected {injected.Length} -> edited {edited.Length} chars)");
        }
        catch { }
    }

    public List<(string Raw, string Refined, string Edited)> LoadPairs()
    {
        var list = new List<(string, string, string)>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            bool legacyPlaintext = false;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Pair? p;
                try
                {
                    p = JsonSerializer.Deserialize<Pair>(Unprotect(line));
                }
                catch
                {
                    p = JsonSerializer.Deserialize<Pair>(line);
                    legacyPlaintext = true;
                }
                if (p is { r.Length: > 0, c.Length: > 0 }) list.Add((p.raw ?? string.Empty, p.r, p.c));
            }
            if (legacyPlaintext) WritePairs(list.TakeLast(MaxEntries));
        }
        catch { }
        return list;
    }

    public void Clear() { try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { } }

    private static void WritePairs(IEnumerable<(string Raw, string Refined, string Edited)> pairs)
    {
        var lines = pairs.Select(p => Protect(JsonSerializer.Serialize(new Pair(p.Raw, p.Refined, p.Edited))));
        string temp = FilePath + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, FilePath, overwrite: true);
    }

    private static string Protect(string plaintext)
    {
        byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string cipher)
    {
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? Limit(string? value) =>
        value is null || value.Length <= MaxTextLength ? value : value[..MaxTextLength];

    private sealed record Pair(string? raw, string r, string c);
}
