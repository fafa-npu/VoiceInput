using System.Net;
using System.Net.Http;
using System.Text;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class OpenAiTranscribeEngineTests
{
    [Fact]
    public async Task VocabularyEntriesAreIncludedInMultipartPrompt()
    {
        string[] entries = ["Contoso", "Project Falcon"];
        Dictionary<string, string>? fields = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            entries,
            async (request, _) =>
            {
                fields = await ReadTextFieldsAsync(request);
                return JsonResponse(HttpStatusCode.OK, """{"text":"done"}""");
            });
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(1000));

        await engine.StopAsync();

        string prompt = Assert.IsType<string>(fields?["prompt"]);
        Assert.Equal(RecognitionVocabulary.BuildPrompt(entries), prompt);
        Assert.All(entries, entry => Assert.Contains(entry, prompt));
    }

    [Fact]
    public async Task EmptyVocabularyOmitsMultipartPrompt()
    {
        Dictionary<string, string>? fields = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            [],
            async (request, _) =>
            {
                fields = await ReadTextFieldsAsync(request);
                return JsonResponse(HttpStatusCode.OK, """{"text":"done"}""");
            });
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(1000));

        await engine.StopAsync();

        Assert.NotNull(fields);
        Assert.False(fields.ContainsKey("prompt"));
    }

    [Fact]
    public async Task HttpFailureExposesOnlySafeCodeAndRequestId()
    {
        const string sentinel = "VOCABULARY SENTINEL!";
        const string body = """{"error":{"code":"invalid_request","message":"VOCABULARY SENTINEL!"}}""";
        var response = JsonResponse(HttpStatusCode.BadRequest, body);
        response.Headers.Add("x-request-id", "request-123");
        string safeFailure = OpenAiTranscribeEngine.FormatHttpFailure(response, body);
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            [sentinel],
            (_, _) => Task.FromResult(response));
        SpeechFault? fault = null;
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(1000));

        await engine.StopAsync();

        Assert.Equal("status=400 code=invalid_request requestId=request-123", safeFailure);
        Assert.DoesNotContain(sentinel, safeFailure);
        Assert.Equal(safeFailure, Assert.IsType<string>(fault?.Detail));
        Assert.DoesNotContain(sentinel, fault.Detail);
    }

    [Fact]
    public void HttpFailureFormatterRejectsUnsafeIdentifiers()
    {
        const string sentinel = "VOCABULARY SENTINEL!";
        const string body = """{"error":{"code":"invalid VOCABULARY SENTINEL!","message":"VOCABULARY SENTINEL!"}}""";
        using var response = JsonResponse(HttpStatusCode.BadRequest, body);
        response.Headers.Add("x-request-id", "request VOCABULARY SENTINEL!");

        string safeFailure = OpenAiTranscribeEngine.FormatHttpFailure(response, body);

        Assert.Equal("status=400", safeFailure);
        Assert.DoesNotContain(sentinel, safeFailure);
    }

    [Fact]
    public void HttpFailureFormatterIgnoresJsonWithoutErrorObject()
    {
        const string sentinel = "VOCABULARY SENTINEL!";
        using var response = JsonResponse(HttpStatusCode.BadRequest, $"[\"{sentinel}\"]");

        string safeFailure = OpenAiTranscribeEngine.FormatHttpFailure(
            response,
            $"[\"{sentinel}\"]");

        Assert.Equal("status=400", safeFailure);
        Assert.DoesNotContain(sentinel, safeFailure);
    }

    [Fact]
    public async Task TooShortCaptureReportsFaultWithoutTranscriptionRequest()
    {
        bool authCalled = false;
        using var engine = new OpenAiTranscribeEngine("http://localhost/transcribe", (_, _) =>
        {
            authCalled = true;
            return Task.CompletedTask;
        });
        SpeechFault? fault = null;
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed(new byte[OpenAiTranscribeEngine.MinimumPcmBytes - 1]);

        await engine.StopAsync();

        Assert.False(authCalled);
        Assert.NotNull(fault);
        Assert.Equal(
            "Recording was too short to transcribe. Speak for at least half a second and try again.",
            fault.UserMessage);
    }

    [Fact]
    public async Task SilentCaptureSkipsTranscriptionRequestEvenWithVocabulary()
    {
        bool requestSent = false;
        string? final = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            ["Contoso", "Project Falcon"],
            (_, _) =>
            {
                requestSent = true;
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    "{\"text\":\"Contoso Project Falcon\"}"));
            });
        engine.Final += value => final = value;
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(64));

        await engine.StopAsync();

        Assert.False(requestSent);
        Assert.Null(final);
    }

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
        engine.Feed(PcmWithAmplitude(1000));

        Task stop = engine.StopAsync();
        await authStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) engine.Cancel();
        })));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(fault);
    }

    private static async Task<Dictionary<string, string>> ReadTextFieldsAsync(HttpRequestMessage request)
    {
        var fields = new Dictionary<string, string>();
        var form = Assert.IsType<MultipartFormDataContent>(request.Content);
        foreach (HttpContent part in form)
        {
            string? name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (name is not null && name != "file")
                fields[name] = await part.ReadAsStringAsync();
        }
        return fields;
    }

    private static byte[] PcmWithAmplitude(short amplitude)
    {
        var pcm = new byte[OpenAiTranscribeEngine.MinimumPcmBytes];
        for (int offset = 0; offset < pcm.Length; offset += 2)
        {
            pcm[offset] = (byte)(amplitude & 0xff);
            pcm[offset + 1] = (byte)(amplitude >> 8);
        }
        return pcm;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
