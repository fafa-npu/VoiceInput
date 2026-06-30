using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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

    public void Arm(string raw, string injected) { _raw = raw; _injected = injected; }

    public async Task CaptureAsync(ContextReader reader)
    {
        var raw = _raw;
        var injected = _injected;
        _raw = _injected = null;   // one-shot
        if (string.IsNullOrWhiteSpace(injected)) return;

        string? edited = (await reader.TryReadFocusedValueAsync())?.Trim();
        if (string.IsNullOrWhiteSpace(edited) || edited == injected) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(new Pair(raw ?? string.Empty, injected, edited)) + Environment.NewLine);
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
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = JsonSerializer.Deserialize<Pair>(line);
                if (p is { r.Length: > 0, c.Length: > 0 }) list.Add((p.raw ?? string.Empty, p.r, p.c));
            }
        }
        catch { }
        return list;
    }

    public void Clear() { try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { } }

    private sealed record Pair(string? raw, string r, string c);
}
