using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class Qwen3AsrEngineTests
{
    [Fact]
    public async Task BuffersPcmAndRaisesTrimmedFinalText()
    {
        byte[]? receivedPcm = null;
        string? receivedLanguage = null;
        IReadOnlyList<string>? receivedVocabulary = null;
        string? final = null;
        using var engine = new Qwen3AsrEngine(
            Resolved(),
            (model, pcm, language, vocabulary, _) =>
            {
                Assert.Equal("qwen3-asr-0.6b-int8", model.Definition.Id);
                receivedPcm = pcm;
                receivedLanguage = language;
                receivedVocabulary = vocabulary;
                return Task.FromResult("  测试文本  ");
            },
            ["Codex", "Qwen3-ASR"]);
        engine.Final += value => final = value;

        await engine.StartAsync("zh-CN");
        engine.Feed([0, 1]);
        engine.Feed([2, 3]);
        await engine.StopAsync();

        Assert.Equal([0, 1, 2, 3], receivedPcm);
        Assert.Equal("zh-CN", receivedLanguage);
        Assert.Equal(["Codex", "Qwen3-ASR"], receivedVocabulary);
        Assert.Equal("测试文本", final);
    }

    [Fact]
    public async Task CancelBeforeStopSkipsInference()
    {
        int calls = 0;
        using var engine = new Qwen3AsrEngine(
            Resolved(),
            (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult("ignored");
            },
            []);

        await engine.StartAsync("en-US");
        engine.Feed([0, 0]);
        engine.Cancel();
        await engine.StopAsync();

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FailureRaisesServiceFault()
    {
        SpeechFault? fault = null;
        using var engine = new Qwen3AsrEngine(
            Resolved(),
            (_, _, _, _, _) => throw new InvalidOperationException("decode failed"),
            []);
        engine.Fault += value => fault = value;

        await engine.StartAsync("zh-CN");
        engine.Feed([0, 0]);
        await engine.StopAsync();

        Assert.Equal(SpeechFaultKind.Service, fault?.Kind);
        Assert.Contains("decode failed", fault?.Detail);
    }

    [Fact]
    public async Task RejectsAudioThatWouldOverflowTheFixedDecoderContext()
    {
        int calls = 0;
        SpeechFault? fault = null;
        using var engine = new Qwen3AsrEngine(
            Resolved(),
            (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult("ignored");
            },
            []);
        engine.Fault += value => fault = value;

        await engine.StartAsync("zh-CN");
        engine.Feed(new byte[(Qwen3AsrEngine.MaxAudioSeconds + 1) * 16_000 * sizeof(short)]);
        await engine.StopAsync();

        Assert.Equal(0, calls);
        Assert.Equal(SpeechFaultKind.Service, fault?.Kind);
        Assert.Contains($"{Qwen3AsrEngine.MaxAudioSeconds} seconds", fault?.UserMessage);
    }

    [Fact]
    public void ReportsBatchCapabilities()
    {
        using var engine = new Qwen3AsrEngine(
            Resolved(),
            (_, _, _, _, _) => Task.FromResult(string.Empty),
            []);

        Assert.True(engine.NeedsAudioFeed);
        Assert.False(engine.HasInterimResults);
        Assert.Equal(120_000, engine.StopTimeoutMs);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(16, 4)]
    public void ChoosesVmFriendlyThreadCount(int processors, int expected) =>
        Assert.Equal(expected, Qwen3AsrRecognizerHost.RecommendedThreadCount(processors));

    [Fact]
    public void ConvertsPcm16LittleEndianToFloat()
    {
        float[] samples = Qwen3AsrRecognizerHost.DecodePcm16([0x00, 0x80, 0xFF, 0x7F, 0x01]);

        Assert.Equal(2, samples.Length);
        Assert.Equal(-1f, samples[0]);
        Assert.InRange(samples[1], 0.9999f, 1f);
    }

    [Fact]
    public void LeavesPathsUnchangedOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        const string path = "/tmp/语音/qwen.onnx";
        Assert.Equal(path, Qwen3AsrRecognizerHost.SherpaPath(path));
    }

    [Fact]
    public async Task HostRejectsWorkAfterDisposeWithoutLoadingNativeModel()
    {
        var host = new Qwen3AsrRecognizerHost();
        host.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.TranscribeAsync(
            Resolved(), [0, 0], "zh-CN", [], CancellationToken.None));
    }

    [Fact]
    public void BoundsAndSanitizesVocabularyPrompt()
    {
        string[] terms = Enumerable.Range(1, 30).Select(index => $"Term,{index}").ToArray();

        string result = Qwen3AsrRecognizerHost.FormatVocabulary(terms);

        Assert.DoesNotContain(",,", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Term,1", result, StringComparison.Ordinal);
        Assert.True(result.Split(',').Length <= Qwen3AsrRecognizerHost.MaxVocabularyTerms);
        Assert.True(result.Length <= Qwen3AsrRecognizerHost.MaxVocabularyCharacters);
    }

    private static FunAsrResolvedModel Resolved()
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get("qwen3-asr-0.6b-int8");
        string root = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", model.Id);
        return new(
            model,
            string.Empty,
            string.Empty,
            model.Artifacts.ToDictionary(
                artifact => artifact.RelativePath,
                artifact => Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }
}
