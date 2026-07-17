using System.Text.Json;
using VoiceInput.Models;

namespace VoiceInput.Tests;

public sealed class RecognitionVocabularyTests
{
    [Fact]
    public void FreshSettingsUseSupportedModelAndEmptyVocabulary()
    {
        var settings = new AppSettings();

        Assert.Equal(TranscribeModelKind.Gpt4oTranscribe, settings.TranscribeModelKind);
        Assert.Empty(settings.RecognitionVocabulary);
    }

    [Theory]
    [InlineData(SpeechEngineKind.Azure, TranscribeModelKind.Gpt4oTranscribe, "PhraseList")]
    [InlineData(SpeechEngineKind.Azure, TranscribeModelKind.Gpt4oMiniTranscribe, "PhraseList")]
    [InlineData(SpeechEngineKind.Azure, TranscribeModelKind.Gpt4oTranscribeDiarize, "PhraseList")]
    [InlineData(SpeechEngineKind.Azure, TranscribeModelKind.Unknown, "PhraseList")]
    [InlineData(SpeechEngineKind.GptTranscribe, TranscribeModelKind.Gpt4oTranscribe, "Prompt")]
    [InlineData(SpeechEngineKind.GptTranscribe, TranscribeModelKind.Gpt4oMiniTranscribe, "Prompt")]
    [InlineData(SpeechEngineKind.GptTranscribe, TranscribeModelKind.Gpt4oTranscribeDiarize, "None")]
    [InlineData(SpeechEngineKind.GptTranscribe, TranscribeModelKind.Unknown, "None")]
    [InlineData(SpeechEngineKind.Windows, TranscribeModelKind.Gpt4oTranscribe, "None")]
    [InlineData(SpeechEngineKind.FunAsr, TranscribeModelKind.Gpt4oTranscribe, "None")]
    [InlineData((SpeechEngineKind)999, TranscribeModelKind.Gpt4oTranscribe, "None")]
    public void ResolvesCapabilityMatrix(
        SpeechEngineKind engine,
        TranscribeModelKind modelKind,
        string expected)
    {
        Assert.Equal(expected, RecognitionVocabulary.ResolveMode(engine, modelKind).ToString());
    }

    [Fact]
    public void ParseSupportsNewlinesCommasAndSemicolonsAndPreservesFirstOccurrence()
    {
        var result = RecognitionVocabulary.Parse(
            " Alpha \r\n\r\nbeta，ALPHA; Gamma；Delta, Product Name ");

        Assert.Equal(["Alpha", "beta", "Gamma", "Delta", "Product Name"], result.Entries);
        Assert.Equal(7, result.ConfiguredCount);
        Assert.Equal(5, result.AcceptedCount);
        Assert.Equal(2, result.RejectedCount);
    }

    [Fact]
    public void NormalizeRejectsNullBlankAndCaseInsensitiveDuplicates()
    {
        var result = RecognitionVocabulary.Normalize([null, " ", "One", "one", " Two "]);

        Assert.Equal(["One", "Two"], result.Entries);
        Assert.Equal(5, result.ConfiguredCount);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(3, result.RejectedCount);
    }

    [Fact]
    public void EmptyEditorHasNoConfiguredEntries()
    {
        var result = RecognitionVocabulary.Parse(string.Empty);

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.ConfiguredCount);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
    }

    [Fact]
    public void EvaluateReturnsEmptyForNoNormalizedEntries()
    {
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.Azure,
            RecognitionVocabulary = [" ", ""],
        };

        RecognitionVocabularyEvaluation result = RecognitionVocabulary.Evaluate(settings);

        Assert.Equal(RecognitionVocabularyMode.PhraseList, result.Mode);
        Assert.Equal(RecognitionVocabularyAction.Empty, result.Action);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void EvaluateAppliesLargeVocabularyWithoutALocalEntryLimit()
    {
        string[] terms = Terms(250);
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.Azure,
            RecognitionVocabulary = terms,
        };

        RecognitionVocabularyEvaluation result = RecognitionVocabulary.Evaluate(settings);

        Assert.Equal(RecognitionVocabularyMode.PhraseList, result.Mode);
        Assert.Equal(RecognitionVocabularyAction.Applied, result.Action);
        Assert.Equal(terms, result.Entries);
    }

    [Fact]
    public void EvaluateReturnsUnsupportedWithoutEntriesForUnsupportedMode()
    {
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.GptTranscribe,
            TranscribeModelKind = TranscribeModelKind.Gpt4oTranscribeDiarize,
            RecognitionVocabulary = ["Jaws"],
        };

        RecognitionVocabularyEvaluation result = RecognitionVocabulary.Evaluate(settings);

        Assert.Equal(RecognitionVocabularyMode.None, result.Mode);
        Assert.Equal(RecognitionVocabularyAction.Unsupported, result.Action);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void EvaluateAppliesNormalizedEntries()
    {
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.GptTranscribe,
            TranscribeModelKind = TranscribeModelKind.Gpt4oMiniTranscribe,
            RecognitionVocabulary = Terms(125),
        };

        RecognitionVocabularyEvaluation result = RecognitionVocabulary.Evaluate(settings);

        Assert.Equal(RecognitionVocabularyMode.Prompt, result.Mode);
        Assert.Equal(RecognitionVocabularyAction.Applied, result.Action);
        Assert.Equal(125, result.Entries.Length);
    }

    [Fact]
    public void BuildPromptAddsPrefixAndOneEntryPerLine()
    {
        Assert.Equal(
            "Vocabulary and spelling hints:\nJaws\nDaybreak",
            RecognitionVocabulary.BuildPrompt(["Jaws", "Daybreak"]));
    }

    [Fact]
    public void SessionLogIncludesTermsOnlyAsSingleLineJsonInDiagnosticMode()
    {
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.GptTranscribe,
            TranscribeModelKind = TranscribeModelKind.Gpt4oTranscribe,
            RecognitionVocabulary = [" Secret ", "secret", "line\nbreak"],
        };
        RecognitionVocabularyEvaluation evaluation = RecognitionVocabulary.Evaluate(settings);

        string normal = RecognitionVocabulary.FormatSessionLog(settings, evaluation);
        settings.DiagnosticLogging = true;
        string diagnostic = RecognitionVocabulary.FormatSessionLog(settings, evaluation);

        Assert.StartsWith("Vocabulary session ", normal, StringComparison.Ordinal);
        Assert.Contains("engine=GptTranscribe", normal, StringComparison.Ordinal);
        Assert.Contains("modelKind=Gpt4oTranscribe", normal, StringComparison.Ordinal);
        Assert.Contains("mode=Prompt", normal, StringComparison.Ordinal);
        Assert.Contains("termCount=2", normal, StringComparison.Ordinal);
        Assert.Contains("action=applied", normal, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", normal, StringComparison.Ordinal);
        Assert.DoesNotContain("line", normal, StringComparison.Ordinal);
        Assert.Contains($"entries={JsonSerializer.Serialize(evaluation.Entries)}", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', diagnostic);
        Assert.DoesNotContain('\n', diagnostic);
    }

    [Fact]
    public void DiagnosticSessionLogIncludesNormalizedEntriesForUnsupportedVocabulary()
    {
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.Windows,
            RecognitionVocabulary = [" Secret ", "secret"],
        };
        RecognitionVocabularyEvaluation evaluation = RecognitionVocabulary.Evaluate(settings);

        string normal = RecognitionVocabulary.FormatSessionLog(settings, evaluation);
        settings.DiagnosticLogging = true;
        string diagnostic = RecognitionVocabulary.FormatSessionLog(settings, evaluation);

        Assert.Equal(RecognitionVocabularyAction.Unsupported, evaluation.Action);
        Assert.Empty(evaluation.Entries);
        Assert.DoesNotContain("Secret", normal, StringComparison.Ordinal);
        Assert.Contains("entries=[\"Secret\"]", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticSessionLogBoundsLargeVocabularyWithoutChangingAppliedEntries()
    {
        string[] terms = Terms(250);
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.Azure,
            RecognitionVocabulary = terms,
        };
        RecognitionVocabularyEvaluation evaluation = RecognitionVocabulary.Evaluate(settings);

        string normal = RecognitionVocabulary.FormatSessionLog(settings, evaluation);
        settings.DiagnosticLogging = true;
        string diagnostic = RecognitionVocabulary.FormatSessionLog(settings, evaluation);

        Assert.Equal(RecognitionVocabularyAction.Applied, evaluation.Action);
        Assert.Equal(terms, evaluation.Entries);
        Assert.Contains("termCount=250", normal, StringComparison.Ordinal);
        Assert.DoesNotContain("Term 1", normal, StringComparison.Ordinal);
        Assert.Contains("\"Term 100\"", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Term 101\"", diagnostic, StringComparison.Ordinal);
        Assert.Contains("loggedEntries=100 entriesTruncated=true", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticSessionLogBoundsIndividualTermLength()
    {
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.Azure,
            DiagnosticLogging = true,
            RecognitionVocabulary = [new string('x', 10_000)],
        };
        RecognitionVocabularyEvaluation evaluation = RecognitionVocabulary.Evaluate(settings);

        string diagnostic = RecognitionVocabulary.FormatSessionLog(settings, evaluation);

        Assert.True(diagnostic.Length < 1_000);
        Assert.Contains("loggedEntries=1 entriesTruncated=true", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CloneDeepCopiesRecognitionVocabulary()
    {
        var original = new AppSettings
        {
            TranscribeModelKind = TranscribeModelKind.Gpt4oMiniTranscribe,
            RecognitionVocabulary = ["Jaws"],
        };

        AppSettings clone = original.Clone();
        clone.RecognitionVocabulary[0] = "Changed";

        Assert.NotSame(original.RecognitionVocabulary, clone.RecognitionVocabulary);
        Assert.Equal("Jaws", original.RecognitionVocabulary[0]);
        Assert.Equal(TranscribeModelKind.Gpt4oMiniTranscribe, clone.TranscribeModelKind);
    }

    private static string[] Terms(int count) =>
        Enumerable.Range(1, count).Select(index => $"Term {index}").ToArray();
}
