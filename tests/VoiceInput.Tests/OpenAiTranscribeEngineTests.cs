using System.Net;
using System.Net.Http;
using System.Text;
using Azure.Core;
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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnauthorizedAzureAccountOffersAnExplicitRecoveryPath(HttpStatusCode status)
    {
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            send: (_, _) => Task.FromResult(JsonResponse(status, """{"error":{"code":"denied"}}""")));
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(1000));

        await engine.StopAsync();

        Assert.Equal(SpeechFaultKind.Authentication, fault?.Kind);
        Assert.Contains("Switch Azure account in Settings", fault?.UserMessage);
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
    public async Task TooShortCaptureFinishesStartedAuthenticationWithoutCancellingIt()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthentication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool authenticationCancelled = false;
        int tokenApplications = 0;
        int requests = 0;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) =>
            {
                Interlocked.Increment(ref tokenApplications);
                return Task.CompletedTask;
            },
            send: (_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"text":"unexpected"}"""));
            },
            preauthenticate: async cancellationToken =>
            {
                using var registration = cancellationToken.Register(
                    () => authenticationCancelled = true);
                authenticationStarted.TrySetResult();
                await releaseAuthentication.Task.WaitAsync(cancellationToken);
            });
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        engine.Feed(new byte[OpenAiTranscribeEngine.MinimumPcmBytes - 1]);

        Task stop = engine.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(authenticationCancelled);
        releaseAuthentication.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(authenticationCancelled);
        Assert.Equal(0, tokenApplications);
        Assert.Equal(0, requests);
        Assert.Equal(SpeechFaultKind.Unknown, fault?.Kind);
    }

    [Fact]
    public async Task SilentCaptureFinishesStartedAuthenticationWithoutCancellingIt()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthentication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool authenticationCancelled = false;
        int tokenApplications = 0;
        int requests = 0;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) =>
            {
                Interlocked.Increment(ref tokenApplications);
                return Task.CompletedTask;
            },
            send: (_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"text":"unexpected"}"""));
            },
            preauthenticate: async cancellationToken =>
            {
                using var registration = cancellationToken.Register(
                    () => authenticationCancelled = true);
                authenticationStarted.TrySetResult();
                await releaseAuthentication.Task.WaitAsync(cancellationToken);
            });
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        engine.Feed(PcmWithAmplitude(64));

        Task stop = engine.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(authenticationCancelled);
        releaseAuthentication.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(authenticationCancelled);
        Assert.Equal(0, tokenApplications);
        Assert.Equal(0, requests);
        Assert.Null(fault);
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

    [Fact]
    public async Task ExplicitCancelAlwaysCancelsStartedPreauthentication()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            preauthenticate: async cancellationToken =>
            {
                authenticationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                        authenticationCancelled.TrySetResult();
                }
            });
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        engine.Cancel();
        await authenticationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await engine.StopAsync();

        Assert.Null(fault);
    }

    [Fact]
    public async Task AuthenticationCompletionTranscribesTheOriginalBufferedRecording()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthentication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] pcm = PcmWithAmplitude(1000);
        byte[]? submittedWav = null;
        int requests = 0;
        string? final = null;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            async (_, cancellationToken) =>
            {
                authenticationStarted.TrySetResult();
                await releaseAuthentication.Task.WaitAsync(cancellationToken);
            },
            send: async (request, cancellationToken) =>
            {
                Interlocked.Increment(ref requests);
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                HttpContent file = Assert.Single(form, part =>
                    part.Headers.ContentDisposition?.Name?.Trim('"') == "file");
                submittedWav = await file.ReadAsByteArrayAsync(cancellationToken);
                return JsonResponse(HttpStatusCode.OK, """{"text":"kept words"}""");
            });
        engine.Final += value => final = value;
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed(pcm);

        Task stop = engine.StopAsync();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, requests);

        releaseAuthentication.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, requests);
        Assert.Equal(PcmWave.Wrap(pcm, AudioCapture.TargetSampleRate), submittedWav);
        Assert.Equal("kept words", final);
        Assert.Null(fault);
    }

    [Fact]
    public async Task AuthenticationBeginsDuringCaptureAndStopReusesThatAttempt()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthentication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] pcm = PcmWithAmplitude(1000);
        byte[]? submittedWav = null;
        int preauthenticationCalls = 0;
        int tokenApplications = 0;
        int requests = 0;
        string? final = null;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) =>
            {
                Interlocked.Increment(ref tokenApplications);
                return Task.CompletedTask;
            },
            send: async (request, cancellationToken) =>
            {
                Interlocked.Increment(ref requests);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("early-access-token", request.Headers.Authorization?.Parameter);
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                HttpContent file = Assert.Single(form, part =>
                    part.Headers.ContentDisposition?.Name?.Trim('"') == "file");
                submittedWav = await file.ReadAsByteArrayAsync(cancellationToken);
                return JsonResponse(HttpStatusCode.OK, """{"text":"early auth words"}""");
            },
            preauthenticateAndGetToken: async cancellationToken =>
            {
                Interlocked.Increment(ref preauthenticationCalls);
                authenticationStarted.TrySetResult();
                await releaseAuthentication.Task.WaitAsync(cancellationToken);
                return AccessTokenFor("early-access-token");
            });
        engine.Final += value => final = value;
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, preauthenticationCalls);
        Assert.Equal(0, tokenApplications);
        engine.Feed(pcm); // audio arrives while the WAM dialog is still open

        Task stop = engine.StopAsync();
        Assert.False(stop.IsCompleted);
        releaseAuthentication.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, preauthenticationCalls);
        Assert.Equal(0, tokenApplications);
        Assert.Equal(1, requests);
        Assert.Equal(PcmWave.Wrap(pcm, AudioCapture.TargetSampleRate), submittedWav);
        Assert.Equal("early auth words", final);
        Assert.Null(fault);
    }

    [Fact]
    public async Task FailedAuthenticationDuringCaptureIsNotRetriedOnStop()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int preauthenticationCalls = 0;
        int tokenApplications = 0;
        int requests = 0;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) =>
            {
                Interlocked.Increment(ref tokenApplications);
                return Task.CompletedTask;
            },
            send: (_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"text":"unexpected"}"""));
            },
            preauthenticate: async cancellationToken =>
            {
                Interlocked.Increment(ref preauthenticationCalls);
                authenticationStarted.TrySetResult();
                await releaseFailure.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("WAM failed");
            });
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(1000));
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task stop = engine.StopAsync();
        releaseFailure.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, preauthenticationCalls);
        Assert.Equal(0, tokenApplications);
        Assert.Equal(0, requests);
        Assert.NotNull(fault);
        Assert.Equal(SpeechFaultKind.Authentication, fault.Kind);
    }

    [Fact]
    public async Task FailedWamTeardownUsesOnlySilentCacheRecoveryForTheBufferedRecording()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int interactiveTokenApplications = 0;
        int silentTokenApplications = 0;
        int requests = 0;
        string? final = null;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) =>
            {
                Interlocked.Increment(ref interactiveTokenApplications);
                return Task.CompletedTask;
            },
            send: (request, _) =>
            {
                Interlocked.Increment(ref requests);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("cached-after-wam", request.Headers.Authorization?.Parameter);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"text":"recovered words"}"""));
            },
            preauthenticateAndGetToken: async cancellationToken =>
            {
                authenticationStarted.TrySetResult();
                await releaseFailure.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("WAM teardown failed");
            },
            getTokenSilently: _ =>
            {
                Interlocked.Increment(ref silentTokenApplications);
                return Task.FromResult(AccessTokenFor("cached-after-wam"));
            });
        engine.Final += value => final = value;
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        engine.Feed(PcmWithAmplitude(1000));

        Task stop = engine.StopAsync();
        releaseFailure.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, interactiveTokenApplications);
        Assert.Equal(1, silentTokenApplications);
        Assert.Equal(1, requests);
        Assert.Equal("recovered words", final);
        Assert.Null(fault);
    }

    [Fact]
    public async Task StopSnapshotsPcmBeforeAuthenticationCompletesAndIgnoresLaterFeed()
    {
        var authenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthentication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] firstPcm = PcmWithAmplitude(1000);
        byte[] latePcm = PcmWithAmplitude(2000);
        byte[]? submittedWav = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask,
            send: async (request, cancellationToken) =>
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                HttpContent file = Assert.Single(form, part =>
                    part.Headers.ContentDisposition?.Name?.Trim('"') == "file");
                submittedWav = await file.ReadAsByteArrayAsync(cancellationToken);
                return JsonResponse(HttpStatusCode.OK, """{"text":"boundary"}""");
            },
            preauthenticateAndGetToken: async cancellationToken =>
            {
                authenticationStarted.TrySetResult();
                await releaseAuthentication.Task.WaitAsync(cancellationToken);
                return AccessTokenFor("boundary-token");
            });
        await engine.StartAsync("en-US");
        engine.Feed(firstPcm);
        engine.BeginAuthentication();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task stop = engine.StopAsync();
        engine.Feed(latePcm);
        releaseAuthentication.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PcmWave.Wrap(firstPcm, AudioCapture.TargetSampleRate), submittedWav);
    }

    [Fact]
    public async Task NearExpiryPreauthenticationUsesOnlySilentRefreshBeforeSubmission()
    {
        int interactiveTokenApplications = 0;
        int silentTokenApplications = 0;
        int requests = 0;
        SpeechFault? fault = null;
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) =>
            {
                Interlocked.Increment(ref interactiveTokenApplications);
                return Task.CompletedTask;
            },
            send: (request, _) =>
            {
                Interlocked.Increment(ref requests);
                Assert.Equal("fresh-token", request.Headers.Authorization?.Parameter);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"text":"refreshed"}"""));
            },
            preauthenticateAndGetToken: _ => Task.FromResult<AccessToken?>(
                AccessTokenFor("expiring-token", TimeSpan.FromSeconds(30))),
            getTokenSilently: _ =>
            {
                Interlocked.Increment(ref silentTokenApplications);
                return Task.FromResult(AccessTokenFor("fresh-token"));
            });
        engine.Fault += value => fault = value;
        await engine.StartAsync("en-US");
        engine.Feed(PcmWithAmplitude(1000));
        engine.BeginAuthentication();

        await engine.StopAsync();

        Assert.Equal(0, interactiveTokenApplications);
        Assert.Equal(1, silentTokenApplications);
        Assert.Equal(1, requests);
        Assert.Null(fault);
    }

    [Fact]
    public void EntraAuthenticationAndTranscriptionHaveOneCombinedStopDeadline()
    {
        using var engine = new OpenAiTranscribeEngine(
            "http://localhost/transcribe",
            (_, _) => Task.CompletedTask);

        Assert.Equal(150_000, engine.StopTimeoutMs);
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

    private static AccessToken AccessTokenFor(string value, TimeSpan? validFor = null) =>
        new(value, DateTimeOffset.UtcNow + (validFor ?? TimeSpan.FromHours(1)));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
