using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class LlmRefinerVocabularyTests
{
    [Theory]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("http://localhost:23333/api/openai/v1", true)]
    [InlineData("http://127.0.0.1:23333/v1", true)]
    [InlineData("http://[::1]:23333/v1", true)]
    [InlineData("http://api.example.com/v1", false)]
    public void AcceptsHttpsAndLocalHttpLlmEndpoints(string baseUrl, bool expected)
    {
        var settings = new AppSettings
        {
            LlmEnabled = true,
            LlmBaseUrl = baseUrl,
            LlmModel = "test-model",
        };

        Assert.Equal(expected, LlmRefiner.IsConfigured(settings));
    }

    [Fact]
    public void ParsesAndNormalizesVocabularyCandidates()
    {
        string[] result = LlmRefiner.ParseVocabularyCandidates("[\"Jaws\",\"jaws\",\" Daybreak \"]");

        Assert.Equal(["Jaws", "Daybreak"], result);
    }

    [Theory]
    [InlineData("{\"term\":\"Jaws\"}")]
    [InlineData("[\"ok\", 42]")]
    [InlineData("[\"Acme, Inc.\"]")]
    [InlineData("[\"Acme；Inc.\"]")]
    [InlineData("[\"Acme\\nInc.\"]")]
    public void RejectsInvalidVocabularyCandidates(string response)
    {
        Assert.Throws<InvalidDataException>(() => LlmRefiner.ParseVocabularyCandidates(response));
    }
}
