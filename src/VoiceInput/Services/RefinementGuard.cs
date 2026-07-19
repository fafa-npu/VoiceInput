namespace VoiceInput.Services;

internal static class RefinementGuard
{
    public static bool IsSafe(string original, string refined, bool allowTransformation = false)
    {
        if (string.IsNullOrWhiteSpace(refined)) return false;
        long maxLength = allowTransformation
            ? Math.Max(original.Length * 8L, original.Length + 500L)
            : Math.Max(original.Length * 2L, original.Length + 80L);
        if (refined.Length > maxLength) return false;
        if (refined.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return false;

        // A non-empty custom prompt is an explicit request to transform the transcript. Keep
        // output-shape protections above, but do not mistake translation or rewriting for drift.
        if (allowTransformation) return true;

        var sourceCjk = CjkCharacters(original);
        if (original.Count(IsCjk) >= 4)
        {
            var outputCjk = CjkCharacters(refined);
            int retainedCjk = sourceCjk.Count(character => outputCjk.Contains(character));
            return retainedCjk >= Math.Max(1, (sourceCjk.Count + 3) / 4);
        }

        var source = Terms(original);
        var output = Terms(refined);
        if (source.Count < 3 || output.Count == 0) return true;
        int retained = source.Count(term => output.Contains(term));
        return retained >= Math.Max(1, source.Count / 4);
    }

    private static HashSet<string> Terms(string text) =>
        text.Split([' ', '\r', '\n', '\t', ',', '.', '，', '。', '!', '?', '！', '？'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => s.Length > 1)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<char> CjkCharacters(string text) => text.Where(IsCjk).ToHashSet();

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF'
        or >= '\u4E00' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF';
}
