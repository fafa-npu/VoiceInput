using VoiceInput.Models;

namespace VoiceInput.Tests;

public sealed class FunAsrModelCatalogTests
{
    [Fact]
    public void DefaultsToSenseVoiceAndHasThreeUniqueModels()
    {
        Assert.Equal("sensevoice-small-q8", FunAsrModelCatalog.Default.Id);
        Assert.Equal(3, FunAsrModelCatalog.Models.Count);
        Assert.Equal(3, FunAsrModelCatalog.Models.Select(model => model.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("sensevoice-small-q8", "vi-VN", false)]
    [InlineData("sensevoice-small-q8", "ko-KR", true)]
    [InlineData("paraformer-zh-q8", "ja-JP", false)]
    [InlineData("fun-asr-nano-q4", "ja-JP", true)]
    public void ReportsLanguageCompatibility(string id, string language, bool expected)
    {
        Assert.Equal(expected, FunAsrModelCatalog.Get(id).Supports(language));
    }

    [Fact]
    public void CatalogPinsEveryArtifact()
    {
        IEnumerable<FunAsrArtifact> artifacts =
            FunAsrModelCatalog.Models.SelectMany(model => model.Artifacts)
                .Append(FunAsrModelCatalog.Runtime)
                .Append(FunAsrModelCatalog.Vad);

        Assert.All(artifacts, artifact =>
        {
            Assert.True(artifact.Size > 0);
            Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
            Assert.Equal(Uri.UriSchemeHttps, artifact.Url.Scheme);
        });
    }
}
