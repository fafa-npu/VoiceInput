using VoiceInput.Models;

namespace VoiceInput.Tests;

public sealed class FunAsrModelCatalogTests
{
    [Fact]
    public void DefaultsToQwenAndKeepsFiveUniqueStableModelIds()
    {
        Assert.Equal(FunAsrModelCatalog.Qwen3AsrId, FunAsrModelCatalog.DefaultId);
        Assert.Equal(FunAsrModelCatalog.Qwen3AsrId, FunAsrModelCatalog.Default.Id);
        Assert.Equal(FunAsrModelCatalog.SenseVoiceId, FunAsrModelCatalog.Models[0].Id);
        Assert.Equal(5, FunAsrModelCatalog.Models.Count);
        Assert.Equal(5, FunAsrModelCatalog.Models.Select(model => model.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("sensevoice-small-q8", "vi-VN", false)]
    [InlineData("sensevoice-small-q8", "ko-KR", true)]
    [InlineData("paraformer-zh-q8", "ja-JP", false)]
    [InlineData("fun-asr-nano-q4", "ja-JP", true)]
    [InlineData("qwen3-asr-0.6b-int8", "vi-VN", true)]
    [InlineData("qwen3-asr-1.7b-int8", "vi-VN", true)]
    public void ReportsLanguageCompatibility(string id, string language, bool expected)
    {
        Assert.Equal(expected, FunAsrModelCatalog.Get(id).Supports(language));
    }

    [Fact]
    public void QwenUsesItsOwnSherpaRuntime()
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(FunAsrModelCatalog.Qwen3AsrId);

        Assert.Equal(FunAsrRunnerKind.Qwen3Asr, model.Runner);
        Assert.False(model.UsesFunAsrRuntime);
        Assert.Equal(6, model.Artifacts.Count);
        Assert.InRange(model.DownloadSize, 980_000_000, 1_000_000_000);
        Assert.Contains("csukuangfj2", model.Source.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Qwen17UsesPinnedModelScopeInt8Package()
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(FunAsrModelCatalog.Qwen3Asr17BId);

        Assert.Equal("Qwen3-ASR 1.7B", model.DisplayName);
        Assert.Equal(FunAsrRunnerKind.Qwen3Asr, model.Runner);
        Assert.False(model.UsesFunAsrRuntime);
        Assert.Equal(6, model.Artifacts.Count);
        Assert.Equal(2_404_222_421, model.DownloadSize);
        Assert.All(model.Artifacts, artifact =>
        {
            Assert.StartsWith($"models/{FunAsrModelCatalog.Qwen3Asr17BId}/", artifact.RelativePath);
            Assert.Contains(
                "cb045ad80b8970c9d411d463e5b78991a566596c",
                artifact.Url.AbsoluteUri,
                StringComparison.Ordinal);
        });
        Assert.EndsWith(
            "/model_1.7B/decoder.int8.onnx",
            model.Artifacts.Single(artifact => artifact.RelativePath.EndsWith("decoder.int8.onnx")).Url.AbsolutePath);
        Assert.EndsWith(
            "/tokenizer/tokenizer_config.json",
            model.Artifacts.Single(artifact => artifact.RelativePath.EndsWith("tokenizer_config.json")).Url.AbsolutePath);
    }

    [Theory]
    [InlineData("conv_frontend.onnx", 48_080_441, "fa894a4ba53da6a4238f2a6ca0b09362e505d39cecbd646051b033e2e8d7e2fb")]
    [InlineData("encoder.int8.onnx", 314_222_162, "436fbd910a0c8914851e5ac1354e807be9f283d08a5da728adaa609731c41469")]
    [InlineData("decoder.int8.onnx", 2_037_458_645, "c43c853fa6e97d08365cb8a5502b360b595cd43c00dc60e4d8ca7cc18cad460b")]
    [InlineData("tokenizer/merges.txt", 1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5")]
    [InlineData("tokenizer/tokenizer_config.json", 12_487, "4942d005604266809309cabc9f4e9cb89ce855d59b14681fdc0e1cc62ea26c4c")]
    [InlineData("tokenizer/vocab.json", 2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910")]
    public void Qwen17PinsEveryArtifact(string suffix, long size, string sha256)
    {
        FunAsrArtifact artifact = FunAsrModelCatalog.Get(FunAsrModelCatalog.Qwen3Asr17BId)
            .Artifacts.Single(candidate => candidate.RelativePath.EndsWith(suffix, StringComparison.Ordinal));

        Assert.Equal(size, artifact.Size);
        Assert.Equal(sha256, artifact.Sha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-model")]
    public void MissingOrInvalidSelectionFallsBackToQwen(string? id) =>
        Assert.Equal(FunAsrModelCatalog.Qwen3AsrId, FunAsrModelCatalog.NormalizeId(id));

    [Fact]
    public void ExplicitSenseVoiceSelectionIsPreserved() =>
        Assert.Equal(
            FunAsrModelCatalog.SenseVoiceId,
            FunAsrModelCatalog.NormalizeId(FunAsrModelCatalog.SenseVoiceId));

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
        IReadOnlyList<string> modelPaths = FunAsrModelCatalog.Models
            .SelectMany(model => model.Artifacts)
            .Select(artifact => artifact.RelativePath)
            .ToArray();
        Assert.Equal(modelPaths.Count, modelPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
