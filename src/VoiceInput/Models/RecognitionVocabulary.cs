using System.Text.Json;

namespace VoiceInput.Models;

public enum TranscribeModelKind
{
    Gpt4oTranscribe,
    Gpt4oMiniTranscribe,
    Gpt4oTranscribeDiarize,
    Unknown,
}

internal enum RecognitionVocabularyMode
{
    None,
    Prompt,
    PhraseList,
}

internal enum RecognitionVocabularyAction
{
    Empty,
    Applied,
    Unsupported,
}

internal sealed record RecognitionVocabularyNormalization(
    string[] Entries,
    int ConfiguredCount,
    int RejectedCount)
{
    public int AcceptedCount => Entries.Length;
}

internal sealed record RecognitionVocabularyEvaluation(
    RecognitionVocabularyMode Mode,
    RecognitionVocabularyAction Action,
    string[] Entries);

internal static class RecognitionVocabulary
{
    internal const int DiagnosticLogEntryLimit = 100;
    internal const int DiagnosticLogTermCharacterLimit = 256;

    private const string PromptHeader = "Vocabulary and spelling hints:";
    private static readonly string[] Separators = ["\r\n", "\n", "\r", ",", "，", ";", "；"];

    internal static RecognitionVocabularyNormalization Parse(string text) =>
        string.IsNullOrEmpty(text)
            ? Normalize([])
            : Normalize(text.Split(Separators, StringSplitOptions.None));

    internal static RecognitionVocabularyNormalization Normalize(IEnumerable<string?> values)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int configuredCount = 0;

        foreach (string? value in values)
        {
            configuredCount++;
            string? entry = value?.Trim();
            if (!string.IsNullOrEmpty(entry) && seen.Add(entry))
            {
                entries.Add(entry);
            }
        }

        return new(entries.ToArray(), configuredCount, configuredCount - entries.Count);
    }

    internal static RecognitionVocabularyMode ResolveMode(
        SpeechEngineKind engine,
        TranscribeModelKind modelKind) =>
        (engine, modelKind) switch
        {
            (SpeechEngineKind.Azure, _) => RecognitionVocabularyMode.PhraseList,
            (SpeechEngineKind.GptTranscribe, TranscribeModelKind.Gpt4oTranscribe or
                TranscribeModelKind.Gpt4oMiniTranscribe) => RecognitionVocabularyMode.Prompt,
            _ => RecognitionVocabularyMode.None,
        };

    internal static RecognitionVocabularyMode ResolveMode(AppSettings settings)
    {
        if (settings.Engine == SpeechEngineKind.FunAsr
            && FunAsrModelCatalog.Get(FunAsrModelCatalog.NormalizeId(settings.FunAsrModelId)).Runner
                == FunAsrRunnerKind.Qwen3Asr)
        {
            return RecognitionVocabularyMode.Prompt;
        }
        return ResolveMode(settings.Engine, settings.TranscribeModelKind);
    }

    internal static RecognitionVocabularyEvaluation Evaluate(AppSettings settings)
    {
        RecognitionVocabularyNormalization normalized = Normalize(settings.RecognitionVocabulary);
        RecognitionVocabularyMode mode = ResolveMode(settings);
        RecognitionVocabularyAction action = normalized.AcceptedCount switch
        {
            0 => RecognitionVocabularyAction.Empty,
            _ when mode == RecognitionVocabularyMode.None => RecognitionVocabularyAction.Unsupported,
            _ => RecognitionVocabularyAction.Applied,
        };

        return new(mode, action, action == RecognitionVocabularyAction.Applied ? normalized.Entries : []);
    }

    internal static string BuildPrompt(IEnumerable<string> entries) =>
        string.Join("\n", entries.Prepend(PromptHeader));

    internal static string FormatSessionLog(
        AppSettings settings,
        RecognitionVocabularyEvaluation evaluation)
    {
        RecognitionVocabularyNormalization normalized = Normalize(settings.RecognitionVocabulary);
        string message =
            $"Vocabulary session engine={settings.Engine} modelKind={settings.TranscribeModelKind} " +
            $"mode={evaluation.Mode} termCount={normalized.AcceptedCount} action={evaluation.Action.ToString().ToLowerInvariant()}";

        if (!settings.DiagnosticLogging)
            return message;

        string entriesJson = FormatDiagnosticEntries(
            normalized.Entries,
            out int loggedEntries,
            out bool entriesTruncated);
        return $"{message} entries={entriesJson} loggedEntries={loggedEntries} "
            + $"entriesTruncated={(entriesTruncated ? "true" : "false")}";
    }

    private static string FormatDiagnosticEntries(
        string[] entries,
        out int loggedEntries,
        out bool truncated)
    {
        loggedEntries = Math.Min(entries.Length, DiagnosticLogEntryLimit);
        truncated = loggedEntries < entries.Length;
        var bounded = new string[loggedEntries];

        for (int index = 0; index < loggedEntries; index++)
        {
            string entry = entries[index];
            if (entry.Length > DiagnosticLogTermCharacterLimit)
            {
                entry = entry[..DiagnosticLogTermCharacterLimit];
                truncated = true;
            }
            bounded[index] = entry;
        }

        return JsonSerializer.Serialize(bounded);
    }
}
