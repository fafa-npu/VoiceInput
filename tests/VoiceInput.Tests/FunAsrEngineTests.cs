using System.Diagnostics;
using System.Text;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class FunAsrEngineTests
{
    [Fact]
    public void NativeProcessUsesUtf8Output()
    {
        ProcessStartInfo startInfo = FunAsrProcess.CreateStartInfo(
            Resolved(FunAsrModelCatalog.SenseVoiceId), "sample.wav");

        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
    }

    [Theory]
    [InlineData("sensevoice-small-q8", "llama-funasr-sensevoice.exe")]
    [InlineData("paraformer-zh-q8", "llama-funasr-paraformer.exe")]
    [InlineData("fun-asr-nano-q4", "llama-funasr-cli.exe")]
    public async Task UsesCatalogExecutable(string modelId, string executable)
    {
        ProcessStartInfo? launched = null;
        using var engine = new FunAsrEngine(
            Resolved(modelId),
            (startInfo, _) =>
            {
                launched = startInfo;
                return Task.FromResult(new FunAsrProcessResult(0, string.Empty, string.Empty));
            });

        await engine.StartAsync("zh-CN");
        engine.Feed([1, 2]);
        await engine.StopAsync();

        Assert.NotNull(launched);
        Assert.EndsWith(executable, launched.FileName);
        AssertCommand(modelId, launched.ArgumentList);
    }

    [Fact]
    public async Task EmptyRecordingDoesNotLaunchProcess()
    {
        int launches = 0;
        using var engine = new FunAsrEngine(
            Resolved(FunAsrModelCatalog.SenseVoiceId),
            (_, _) =>
            {
                launches++;
                return Task.FromResult(new FunAsrProcessResult(0, string.Empty, string.Empty));
            });

        await engine.StartAsync("zh-CN");
        await engine.StopAsync();

        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task SuccessfulProcessRaisesTrimmedFinalTextAndDeletesWaveFile()
    {
        string? wavePath = null;
        string? final = null;
        using var engine = new FunAsrEngine(
            Resolved(FunAsrModelCatalog.SenseVoiceId),
            (startInfo, _) =>
            {
                wavePath = ArgumentAfter(startInfo.ArgumentList, "-a");
                Assert.True(File.Exists(wavePath));
                Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(wavePath), 0, 4));
                return Task.FromResult(new FunAsrProcessResult(0, "  recognized text\r\n", string.Empty));
            });
        engine.Final += value => final = value;

        await engine.StartAsync("zh-CN");
        engine.Feed([1, 2, 3, 4]);
        await engine.StopAsync();

        Assert.Equal("recognized text", final);
        Assert.NotNull(wavePath);
        Assert.False(File.Exists(wavePath));
    }

    [Fact]
    public async Task NonzeroExitRaisesServiceFaultWithoutFinalText()
    {
        string? final = null;
        SpeechFault? fault = null;
        using var engine = new FunAsrEngine(
            Resolved(FunAsrModelCatalog.SenseVoiceId),
            (_, _) => Task.FromResult(new FunAsrProcessResult(7, "partial text", "model failed")));
        engine.Final += value => final = value;
        engine.Fault += value => fault = value;

        await engine.StartAsync("zh-CN");
        engine.Feed([1, 2]);
        await engine.StopAsync();

        Assert.Null(final);
        Assert.Equal(SpeechFaultKind.Service, fault?.Kind);
        Assert.Contains("exit code 7", fault?.Detail);
    }

    [Fact]
    public async Task CancelStopsProcessAndRaisesNoFault()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? final = null;
        SpeechFault? fault = null;
        using var engine = new FunAsrEngine(
            Resolved(FunAsrModelCatalog.SenseVoiceId),
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new FunAsrProcessResult(0, "should not be returned", string.Empty);
            });
        engine.Final += value => final = value;
        engine.Fault += value => fault = value;
        await engine.StartAsync("zh-CN");
        engine.Feed([1, 2]);

        Task stopping = engine.StopAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.Cancel();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(final);
        Assert.Null(fault);
    }

    [Fact]
    public async Task DisposeCancelsRunningProcessAndCleansWaveBeforeReturning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? wavePath = null;
        var engine = new FunAsrEngine(
            Resolved(FunAsrModelCatalog.SenseVoiceId),
            async (startInfo, cancellationToken) =>
            {
                wavePath = ArgumentAfter(startInfo.ArgumentList, "-a");
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new FunAsrProcessResult(0, string.Empty, string.Empty);
            });
        await engine.StartAsync("zh-CN");
        engine.Feed([1, 2]);
        Task stopping = engine.StopAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(wavePath));

        engine.Dispose();

        Assert.False(File.Exists(wavePath));
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ReportsBatchEngineCapabilities()
    {
        using var engine = new FunAsrEngine(
            Resolved(FunAsrModelCatalog.SenseVoiceId),
            (_, _) => Task.FromResult(new FunAsrProcessResult(0, string.Empty, string.Empty)));

        Assert.True(engine.NeedsAudioFeed);
        Assert.False(engine.HasInterimResults);
        Assert.Equal(60_000, engine.StopTimeoutMs);
    }

    private static FunAsrResolvedModel Resolved(string modelId)
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(modelId);
        string executable = model.Runner switch
        {
            FunAsrRunnerKind.SenseVoice => "llama-funasr-sensevoice.exe",
            FunAsrRunnerKind.Paraformer => "llama-funasr-paraformer.exe",
            FunAsrRunnerKind.Nano => "llama-funasr-cli.exe",
            _ => throw new InvalidOperationException(),
        };
        string root = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", modelId);
        return new(
            model,
            Path.Combine(root, executable),
            Path.Combine(root, "fsmn-vad.gguf"),
            model.Artifacts.ToDictionary(
                artifact => artifact.RelativePath,
                artifact => Path.Combine(root, Path.GetFileName(artifact.RelativePath))));
    }

    private static void AssertCommand(string modelId, IList<string> arguments)
    {
        string[] values = arguments.Cast<string>().ToArray();
        if (modelId == "fun-asr-nano-q4")
        {
            Assert.Equal("--enc", values[0]);
            Assert.Contains("encoder", values[1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("-m", values[2]);
            Assert.Contains("qwen", values[3], StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal("-m", values[0]);
            Assert.EndsWith(".gguf", values[1]);
        }

        Assert.Contains("-a", values);
        Assert.Contains("--vad", values);
    }

    private static string ArgumentAfter(IList<string> arguments, string name)
    {
        int index = arguments.IndexOf(name);
        Assert.True(index >= 0);
        return arguments[index + 1]!;
    }
}
