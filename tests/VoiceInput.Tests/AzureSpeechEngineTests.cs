using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class AzureSpeechEngineTests
{
    [Fact]
    public void AddVocabularyPhrasesAddsEveryEntry()
    {
        var added = new List<string>();

        AzureVocabularyApplyResult result = AzureSpeechEngine.AddVocabularyPhrases(
            ["Contoso", "Project Falcon"],
            added.Add);

        Assert.Equal(["Contoso", "Project Falcon"], added);
        Assert.Equal(2, result.AppliedCount);
        Assert.Null(result.ExceptionType);
        Assert.Equal(
            "Vocabulary azure-phrase-list requested=2 applied=2",
            AzureSpeechEngine.FormatVocabularyLog(2, result));
    }

    [Fact]
    public void AddVocabularyPhrasesDoesNothingForEmptyEntries()
    {
        bool called = false;

        AzureVocabularyApplyResult result = AzureSpeechEngine.AddVocabularyPhrases(
            [],
            _ => called = true);

        Assert.False(called);
        Assert.Equal(0, result.AppliedCount);
        Assert.Null(result.ExceptionType);
    }

    [Fact]
    public void AddVocabularyPhrasesStopsAtAzureServiceLimit()
    {
        string[] entries = Enumerable.Range(1, AzureSpeechEngine.MaxVocabularyPhrases + 1)
            .Select(index => $"Term {index}")
            .ToArray();
        var added = new List<string>();

        AzureVocabularyApplyResult result = AzureSpeechEngine.AddVocabularyPhrases(entries, added.Add);

        Assert.Equal(AzureSpeechEngine.MaxVocabularyPhrases, added.Count);
        Assert.Equal("Term 500", added[^1]);
        Assert.Equal(AzureSpeechEngine.MaxVocabularyPhrases, result.AppliedCount);
        Assert.Null(result.ExceptionType);
        Assert.Equal(
            "Vocabulary azure-phrase-list requested=501 applied=500 limit=500",
            AzureSpeechEngine.FormatVocabularyLog(entries.Length, result));
    }

    [Fact]
    public void AddVocabularyPhrasesReturnsPartialCountAndExceptionTypeWithoutMessage()
    {
        const string sentinel = "VOCABULARY_SENTINEL";

        AzureVocabularyApplyResult result = AzureSpeechEngine.AddVocabularyPhrases(
            ["first", "second"],
            phrase =>
            {
                if (phrase == "second")
                    throw new InvalidOperationException(sentinel);
            });

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(nameof(InvalidOperationException), result.ExceptionType);
        Assert.DoesNotContain(sentinel, result.ToString());
        Assert.Equal(
            "WARN Vocabulary azure-phrase-list requested=2 applied=1 exceptionType=InvalidOperationException",
            AzureSpeechEngine.FormatVocabularyLog(2, result));
    }

    [Fact]
    public void EntraStartupAllowsAuthenticationToReachItsOwnDeadline()
    {
        using var key = AzureSpeechEngine.ForKey("key", "westus");
        using var entra = AzureSpeechEngine.ForEntra(
            "https://example.test/",
            new StaticTokenCredential());

        Assert.Equal(8_000, key.StartTimeoutMs);
        Assert.Equal(150_000, entra.StartTimeoutMs);
    }

    [Theory]
    [InlineData(CancellationErrorCode.AuthenticationFailure)]
    [InlineData(CancellationErrorCode.Forbidden)]
    public void EntraAuthorizationFailuresOfferAnExplicitRecoveryPath(CancellationErrorCode code)
    {
        SpeechFault fault = AzureSpeechEngine.MapFault(code, "detail");

        Assert.Equal(SpeechFaultKind.Authentication, fault.Kind);
        Assert.Contains("Switch Azure account in Settings", fault.UserMessage);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new(
            "token",
            DateTimeOffset.UtcNow.AddHours(1));

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }
}
