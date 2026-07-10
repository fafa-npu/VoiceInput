namespace VoiceInput.Services;

internal static class RefinementGuard
{
    public static bool IsSafe(string original, string refined)
    {
        if (string.IsNullOrWhiteSpace(refined)) return false;
        if (refined.Length > Math.Max(original.Length * 2, original.Length + 80)) return false;
        if (refined.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return false;

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
}
