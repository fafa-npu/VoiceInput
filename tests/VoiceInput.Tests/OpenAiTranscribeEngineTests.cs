using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class OpenAiTranscribeEngineTests
{
    [Fact]
    public async Task ConcurrentCancellationDoesNotThrowOrReportFault()
    {
        var authStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new OpenAiTranscribeEngine("http://localhost/transcribe", async (_, token) =>
        {
            authStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        SpeechFault? fault = null;
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed([1, 2]);

        Task stop = engine.StopAsync();
        await authStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) engine.Cancel();
        })));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(fault);
    }
}
