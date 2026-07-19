using VoiceInput.Models;

namespace VoiceInput.Tests;

public sealed class FunAsrModelCatalogTests
{
    [Fact]
    public void DefaultsToSenseVoiceAndHasFourUniqueModels()
    {
        Assert.Equal("sensevoice-small-q8", FunAsrModelCatalog.Default.Id);
        Assert.Equal(4, FunAsrModelCatalog.Models.Count);
        Assert.Equal(4, FunAsrModelCatalog.Models.Select(model => model.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("sensevoice-small-q8", "vi-VN", false)]
    [InlineData("sensevoice-small-q8", "ko-KR", true)]
    [InlineData("paraformer-zh-q8", "ja-JP", false)]
    [InlineData("fun-asr-nano-q4", "ja-JP", true)]
    [InlineData("qwen3-asr-0.6b-int8", "vi-VN", true)]
    public void ReportsLanguageCompatibility(string id, string language, bool expected)
    {
        Assert.Equal(expected, FunAsrModelCatalog.Get(id).Supports(language));
    }

    [Fact]
    public void QwenUsesItsOwnSherpaRuntime()
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get("qwen3-asr-0.6b-int8");

        Assert.Equal(FunAsrRunnerKind.Qwen3Asr, model.Runner);
        Assert.False(model.UsesFunAsrRuntime);
        Assert.Equal(6, model.Artifacts.Count);
        Assert.InRange(model.DownloadSize, 980_000_000, 1_000_000_000);
        Assert.Contains("csukuangfj2", model.Source.AbsoluteUri, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false, "funasr-llamacpp-windows-x64.zip", "runtime-v0.1.5.zip")]
    [InlineData(true, "funasr-llamacpp-windows-x64-avx2.zip", "runtime-v0.1.5-avx2.zip")]
    public void SelectsRuntimeForCpuCapabilities(
        bool avx2Supported, string assetName, string relativePath)
    {
        FunAsrArtifact runtime = FunAsrModelCatalog.SelectRuntime(avx2Supported);

        Assert.EndsWith(assetName, runtime.Url.AbsolutePath);
        Assert.Equal(relativePath, runtime.RelativePath);
    }
}
