using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class EntraCredentialFactoryTests
{
    private static readonly TokenRequestContext Context = new(
        new[] { EntraCredentialFactory.CognitiveServicesScope });

    [Fact]
    public void BrokerOptionsUseSilentServiceCallsAndWindowsAccountOnFirstSignIn()
    {
        var options = EntraCredentialFactory.CreateOptions("tenant-id", null, 0);

        Assert.True(options.DisableAutomaticAuthentication);
        Assert.True(options.UseDefaultBrokerAccount);
        Assert.Equal("tenant-id", options.TenantId);
        Assert.Equal(EntraCredentialFactory.CacheName, options.TokenCachePersistenceOptions.Name);
        Assert.Null(options.AuthenticationRecord);
        Assert.Null(options.LoginHint);
    }

    [Fact]
    public void BrokerOptionsReuseSavedRecordButAllowOneInteractiveRecoveryDialog()
    {
        AuthenticationRecord record = IdentityModelFactory.AuthenticationRecord(
            "person@example.test",
            "https://login.microsoftonline.com/",
            "home-account-id",
            "tenant-id",
            "client-id");

        var options = EntraCredentialFactory.CreateOptions("tenant-id", record, 0);

        Assert.True(options.DisableAutomaticAuthentication);
        Assert.False(options.UseDefaultBrokerAccount);
        Assert.Same(record, options.AuthenticationRecord);
        Assert.Null(options.LoginHint);
    }

    [Fact]
    public void AccountSwitchOptionsForceTheWindowsAccountPicker()
    {
        var options = EntraCredentialFactory.CreateOptions(
            "tenant-id",
            record: null,
            parentWindowHandle: 0,
            useDefaultBrokerAccount: false);

        Assert.True(options.DisableAutomaticAuthentication);
        Assert.False(options.UseDefaultBrokerAccount);
        Assert.Null(options.AuthenticationRecord);
        Assert.Null(options.LoginHint);
    }

    [Fact]
    public void AuthenticationRecordAtomicallyReplacesACorruptSavedRecord()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"voiceinput-entra-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "record.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "not an authentication record");
            Assert.Null(EntraCredentialFactory.TryLoadRecordFile(path));

            AuthenticationRecord expected = IdentityModelFactory.AuthenticationRecord(
                "person@example.test",
                "https://login.microsoftonline.com/",
                "home-account-id",
                "tenant-id",
                "client-id");
            Assert.True(EntraCredentialFactory.TrySaveRecordFile(path, expected));

            AuthenticationRecord actual = Assert.IsType<AuthenticationRecord>(
                EntraCredentialFactory.TryLoadRecordFile(path));
            Assert.Equal(expected.Username, actual.Username);
            Assert.Equal(expected.HomeAccountId, actual.HomeAccountId);
            Assert.Equal(expected.TenantId, actual.TenantId);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedAuthenticationRecordReplacementPreservesTheExistingRecord()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"voiceinput-entra-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "record.bin");
        Directory.CreateDirectory(directory);
        try
        {
            AuthenticationRecord existing = IdentityModelFactory.AuthenticationRecord(
                "existing@example.test",
                "https://login.microsoftonline.com/",
                "existing-home-account-id",
                "tenant-id",
                "client-id");
            AuthenticationRecord replacement = IdentityModelFactory.AuthenticationRecord(
                "replacement@example.test",
                "https://login.microsoftonline.com/",
                "replacement-home-account-id",
                "tenant-id",
                "client-id");
            Assert.True(EntraCredentialFactory.TrySaveRecordFile(path, existing));

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(EntraCredentialFactory.TrySaveRecordFile(path, replacement));
                AuthenticationRecord preserved = Assert.IsType<AuthenticationRecord>(
                    EntraCredentialFactory.TryLoadRecordFile(path));
                Assert.Equal(existing.Username, preserved.Username);
                Assert.Equal(existing.HomeAccountId, preserved.HomeAccountId);
                Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CredentialReplacementCommitsGenerationAndCacheOnlyAfterPersistenceSucceeds()
    {
        const string tenant = "tenant-id";
        var generations = new Dictionary<string, long> { [tenant] = 7 };
        var credentials = new Dictionary<string, string> { [tenant] = "existing-account" };
        long attemptedGeneration = 0;

        bool failed = EntraCredentialFactory.TryCommitCredentialReplacement(
            generations,
            credentials,
            tenant,
            generation =>
            {
                attemptedGeneration = generation;
                return "replacement-account";
            },
            persist: () => false);

        Assert.False(failed);
        Assert.Equal(8, attemptedGeneration);
        Assert.Equal(7, generations[tenant]);
        Assert.Equal("existing-account", credentials[tenant]);
        Assert.True(EntraCredentialFactory.IsCurrentGeneration(generations, tenant, 7));

        bool committed = EntraCredentialFactory.TryCommitCredentialReplacement(
            generations,
            credentials,
            tenant,
            generation => $"replacement-account-{generation}",
            persist: () => true);

        Assert.True(committed);
        Assert.Equal(8, generations[tenant]);
        Assert.Equal("replacement-account-8", credentials[tenant]);
        Assert.False(EntraCredentialFactory.IsCurrentGeneration(generations, tenant, 7));
        Assert.True(EntraCredentialFactory.IsCurrentGeneration(generations, tenant, 8));
        bool staleSaveCalled = false;
        Assert.False(EntraCredentialFactory.TryRunForCurrentGeneration(
            generations,
            tenant,
            generation: 7,
            () =>
            {
                staleSaveCalled = true;
                return true;
            }));
        Assert.False(staleSaveCalled);
    }

    [Fact]
    public async Task CachedTokenReturnsWithoutInteractiveAuthentication()
    {
        var silent = new TestTokenCredential();
        silent.MarkAuthenticated();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(silent, (_, _) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            return Task.CompletedTask;
        });

        AccessToken[] tokens = await Task.WhenAll(
            credential.GetTokenAsync(Context, CancellationToken.None).AsTask(),
            credential.GetTokenAsync(Context, CancellationToken.None).AsTask());

        Assert.Equal(0, authenticationCalls);
        Assert.All(tokens, token => Assert.Equal("access-token", token.Token));
    }

    [Fact]
    public async Task SilentPrewarmNeverInvokesInteractiveAuthentication()
    {
        var silent = new TestTokenCredential();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(silent, (_, _) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<AuthenticationRequiredException>(async () =>
            await credential.GetTokenSilentlyAsync(Context, CancellationToken.None));

        Assert.Equal(0, authenticationCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneInteractiveAuthentication()
    {
        var silent = new TestTokenCredential();
        var authenticationStarted = NewSignal();
        var releaseAuthentication = NewSignal();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(silent, async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            authenticationStarted.TrySetResult();
            await releaseAuthentication.Task.WaitAsync(cancellationToken);
            silent.MarkAuthenticated();
        });

        Task<AccessToken>[] requests = Enumerable.Range(0, 8)
            .Select(_ => credential.GetTokenAsync(Context, CancellationToken.None).AsTask())
            .ToArray();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref authenticationCalls));
        releaseAuthentication.SetResult();
        AccessToken[] tokens = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2));
        AccessToken laterToken = await credential.GetTokenAsync(Context, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, authenticationCalls);
        Assert.All(tokens, token => Assert.Equal("access-token", token.Token));
        Assert.Equal("access-token", laterToken.Token);
    }

    [Fact]
    public async Task CancellingFirstCallerDoesNotCancelAnotherWaiter()
    {
        var secondSilentAttempted = NewSignal();
        int silentCalls = 0;
        var silent = new TestTokenCredential(() =>
        {
            if (Interlocked.Increment(ref silentCalls) >= 2)
                secondSilentAttempted.TrySetResult();
        });
        var authenticationStarted = NewSignal();
        var releaseAuthentication = NewSignal();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(silent, async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            authenticationStarted.TrySetResult();
            await releaseAuthentication.Task.WaitAsync(cancellationToken);
            silent.MarkAuthenticated();
        });
        using var firstCancellation = new CancellationTokenSource();

        Task<AccessToken> first = credential.GetTokenAsync(Context, firstCancellation.Token).AsTask();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<AccessToken> second = credential.GetTokenAsync(Context, CancellationToken.None).AsTask();
        await secondSilentAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(second.IsCompleted);

        releaseAuthentication.SetResult();
        AccessToken token = await second.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("access-token", token.Token);
        Assert.Equal(1, authenticationCalls);
    }

    [Fact]
    public async Task DifferentContextsRetryTheirOwnSilentTokenAfterSerializedAuthentication()
    {
        var contextA = new TokenRequestContext(new[] { "scope-a" });
        var contextB = new TokenRequestContext(
            new[] { "scope-b" },
            parentRequestId: null,
            claims: "{\"access_token\":{\"xms_cc\":{\"values\":[\"cp1\"]}}}",
            tenantId: "tenant-b",
            isCaeEnabled: true);
        var scopeBSeen = NewSignal();
        var silent = new ScopedTokenCredential(scope =>
        {
            if (scope == "scope-b") scopeBSeen.TrySetResult();
        });
        var authenticationAStarted = NewSignal();
        var releaseAuthenticationA = NewSignal();
        int activeAuthentications = 0;
        int maxActiveAuthentications = 0;
        var authenticatedContexts = new ConcurrentQueue<string>();
        var credential = new SingleFlightTokenCredential(silent, async (context, cancellationToken) =>
        {
            int active = Interlocked.Increment(ref activeAuthentications);
            UpdateMaximum(ref maxActiveAuthentications, active);
            string scope = context.Scopes[0];
            authenticatedContexts.Enqueue(scope);
            try
            {
                if (scope == "scope-a")
                {
                    authenticationAStarted.TrySetResult();
                    await releaseAuthenticationA.Task.WaitAsync(cancellationToken);
                }
                silent.MarkAuthenticated(scope);
            }
            finally
            {
                Interlocked.Decrement(ref activeAuthentications);
            }
        });

        Task<AccessToken> requestA = credential.GetTokenAsync(contextA, CancellationToken.None).AsTask();
        await authenticationAStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<AccessToken> requestB = credential.GetTokenAsync(contextB, CancellationToken.None).AsTask();
        await scopeBSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseAuthenticationA.SetResult();

        AccessToken[] tokens = await Task.WhenAll(requestA, requestB).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("token-scope-a", tokens[0].Token);
        Assert.Equal("token-scope-b", tokens[1].Token);
        Assert.Equal(new[] { "scope-a", "scope-b" }, authenticatedContexts);
        Assert.Equal(1, maxActiveAuthentications);
    }

    [Fact]
    public async Task FailureForOneContextDoesNotPoisonAnotherContext()
    {
        var contextA = new TokenRequestContext(new[] { "scope-a" });
        var contextB = new TokenRequestContext(new[] { "scope-b" });
        var scopeBSeen = NewSignal();
        var releaseFailureA = NewSignal();
        var authenticationAStarted = NewSignal();
        var silent = new ScopedTokenCredential(scope =>
        {
            if (scope == "scope-b") scopeBSeen.TrySetResult();
        });
        var credential = new SingleFlightTokenCredential(silent, async (context, cancellationToken) =>
        {
            string scope = context.Scopes[0];
            if (scope == "scope-a")
            {
                authenticationAStarted.TrySetResult();
                await releaseFailureA.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("scope A failed");
            }
            silent.MarkAuthenticated(scope);
        });

        Task<AccessToken> requestA = credential.GetTokenAsync(contextA, CancellationToken.None).AsTask();
        await authenticationAStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<AccessToken> requestB = credential.GetTokenAsync(contextB, CancellationToken.None).AsTask();
        await scopeBSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFailureA.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => requestA);
        AccessToken tokenB = await requestB.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("token-scope-b", tokenB.Token);
    }

    [Fact]
    public async Task ConcurrentCallersShareAnAuthenticationFailureAndALaterCallCanRetry()
    {
        var silent = new TestTokenCredential();
        var authenticationStarted = NewSignal();
        var releaseFailure = NewSignal();
        int authenticationCalls = 0;
        bool fail = true;
        var credential = new SingleFlightTokenCredential(silent, async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            authenticationStarted.TrySetResult();
            await releaseFailure.Task.WaitAsync(cancellationToken);
            if (fail) throw new InvalidOperationException("interactive authentication failed");
            silent.MarkAuthenticated();
        });

        Task<AccessToken>[] requests = Enumerable.Range(0, 4)
            .Select(_ => credential.GetTokenAsync(Context, CancellationToken.None).AsTask())
            .ToArray();
        await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFailure.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, authenticationCalls);

        fail = false;
        AccessToken retry = await credential.GetTokenAsync(Context, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, authenticationCalls);
        Assert.Equal("access-token", retry.Token);
    }

    [Fact]
    public async Task WamTeardownFailureUsesTheTokenThatWasAlreadyCached()
    {
        int silentCalls = 0;
        var silent = new TestTokenCredential(() => Interlocked.Increment(ref silentCalls));
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(silent, (_, _) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            silent.MarkAuthenticated();
            throw new InvalidOperationException("WAM teardown failed");
        });

        AccessToken token = await credential.GetTokenAsync(Context, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("access-token", token.Token);
        Assert.Equal(1, authenticationCalls);
        Assert.Equal(2, silentCalls); // initial cache miss plus the one salvage lookup
    }

    [Fact]
    public async Task SuccessfulInteractiveAttemptIsNeverFollowedByASecondPromptForTheSameRequest()
    {
        var silent = new TestTokenCredential();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(silent, (_, _) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            // Model a broker interaction that returned without making a context-correct token
            // available. The caller may fail, but must not open another dialog in the same request.
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<AuthenticationRequiredException>(async () =>
            await credential.GetTokenAsync(Context, CancellationToken.None));

        Assert.Equal(1, authenticationCalls);
    }

    [Fact]
    public async Task LastWaiterCancellationDoesNotOverlapTheBrokerAndRecoversAfterItExits()
    {
        var silent = new TestTokenCredential();
        var gate = new RecoverableSemaphoreGate();
        var firstAuthenticationStarted = NewSignal();
        var allowFirstAuthenticationToExit = NewSignal();
        var firstAuthenticationExited = NewSignal();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(
            silent,
            async (_, cancellationToken) =>
            {
                int call;
                using (await gate.EnterAsync(cancellationToken))
                {
                    call = Interlocked.Increment(ref authenticationCalls);
                    if (call == 1)
                    {
                        firstAuthenticationStarted.TrySetResult();
                        await allowFirstAuthenticationToExit.Task; // delayed broker cleanup
                    }
                    else
                    {
                        silent.MarkAuthenticated();
                    }
                }
                if (call == 1) firstAuthenticationExited.TrySetResult();
            },
            authenticationTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();

        Task<AccessToken> first = credential.GetTokenAsync(Context, cancellation.Token).AsTask();
        await firstAuthenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(2)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await credential.GetTokenAsync(Context, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, authenticationCalls);

        allowFirstAuthenticationToExit.SetResult();
        await firstAuthenticationExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AccessToken later = await credential.GetTokenAsync(Context, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, authenticationCalls);
        Assert.Equal("access-token", later.Token);
    }

    [Fact]
    public async Task HardTimeoutDoesNotOverlapTheBrokerAndRecoversAfterItExits()
    {
        var silent = new TestTokenCredential();
        var gate = new RecoverableSemaphoreGate();
        var firstAuthenticationStarted = NewSignal();
        var allowFirstAuthenticationToExit = NewSignal();
        var firstAuthenticationExited = NewSignal();
        int authenticationCalls = 0;
        var credential = new SingleFlightTokenCredential(
            silent,
            async (_, cancellationToken) =>
            {
                int call;
                using (await gate.EnterAsync(cancellationToken))
                {
                    call = Interlocked.Increment(ref authenticationCalls);
                    if (call == 1)
                    {
                        firstAuthenticationStarted.TrySetResult();
                        await allowFirstAuthenticationToExit.Task; // deliberately ignores cancellation
                    }
                    else
                    {
                        silent.MarkAuthenticated();
                    }
                }
                if (call == 1) firstAuthenticationExited.TrySetResult();
            },
            authenticationTimeout: TimeSpan.FromMilliseconds(150));

        Task<AccessToken> first = credential.GetTokenAsync(Context, CancellationToken.None).AsTask();
        await firstAuthenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task completed = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(first, completed);
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await credential.GetTokenAsync(Context, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, authenticationCalls);

        allowFirstAuthenticationToExit.SetResult();
        await firstAuthenticationExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AccessToken later = await credential.GetTokenAsync(Context, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("access-token", later.Token);
        Assert.Equal(2, authenticationCalls);
    }

    [Fact]
    public async Task OwnerCancellationAndReleaseCannotLeakAQueuedSemaphorePermit()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var gate = new RecoverableSemaphoreGate();
            using var ownerCancellation = new CancellationTokenSource();
            IDisposable owner = await gate.EnterAsync(ownerCancellation.Token);
            Task<IDisposable> queued = gate.EnterAsync(CancellationToken.None).AsTask();
            Assert.False(queued.IsCompleted);

            ownerCancellation.Cancel();
            owner.Dispose();

            IDisposable? queuedLease = null;
            try
            {
                queuedLease = await queued.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (InvalidOperationException)
            {
                // The unavailable signal may win even though the owner exits immediately.
            }
            queuedLease?.Dispose();

            using IDisposable recovered = await gate.EnterAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task QueuedWaiterObservesCancellationAfterANormalOwnerHandoff()
    {
        var gate = new RecoverableSemaphoreGate();
        IDisposable first = await gate.EnterAsync(CancellationToken.None);
        using var secondCancellation = new CancellationTokenSource();
        using var thirdCancellation = new CancellationTokenSource();
        Task<IDisposable> secondTask = gate.EnterAsync(secondCancellation.Token).AsTask();
        Task<IDisposable> thirdTask = gate.EnterAsync(thirdCancellation.Token).AsTask();
        Assert.False(secondTask.IsCompleted);
        Assert.False(thirdTask.IsCompleted);

        first.Dispose();
        Task<IDisposable> ownerTask = await Task.WhenAny(secondTask, thirdTask)
            .WaitAsync(TimeSpan.FromSeconds(2));
        IDisposable owner = await ownerTask;
        Task<IDisposable> queuedTask;
        CancellationTokenSource ownerCancellation;
        if (ReferenceEquals(ownerTask, secondTask))
        {
            ownerCancellation = secondCancellation;
            queuedTask = thirdTask;
        }
        else
        {
            ownerCancellation = thirdCancellation;
            queuedTask = secondTask;
        }
        Assert.False(queuedTask.IsCompleted);

        ownerCancellation.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await queuedTask.WaitAsync(TimeSpan.FromSeconds(2)));

        owner.Dispose();
        using IDisposable recovered = await gate.EnterAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maximum);
            if (candidate <= current) return;
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, current) != current);
    }

    private sealed class TestTokenCredential(Action? onRequest = null) : TokenCredential
    {
        private bool _authenticated;

        public void MarkAuthenticated() => Volatile.Write(ref _authenticated, true);

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onRequest?.Invoke();
            if (!Volatile.Read(ref _authenticated))
            {
                return ValueTask.FromException<AccessToken>(
                    new AuthenticationRequiredException("Interactive authentication required.", requestContext));
            }
            return ValueTask.FromResult(new AccessToken(
                "access-token",
                DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class ScopedTokenCredential(Action<string>? onRequest = null) : TokenCredential
    {
        private readonly ConcurrentDictionary<string, bool> _authenticated = new();

        public void MarkAuthenticated(string scope) => _authenticated[scope] = true;

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string scope = requestContext.Scopes[0];
            onRequest?.Invoke(scope);
            return _authenticated.ContainsKey(scope)
                ? ValueTask.FromResult(new AccessToken(
                    $"token-{scope}",
                    DateTimeOffset.UtcNow.AddHours(1)))
                : ValueTask.FromException<AccessToken>(
                    new AuthenticationRequiredException("Interactive authentication required.", requestContext));
        }
    }
}
