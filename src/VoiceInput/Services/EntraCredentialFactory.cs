using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace VoiceInput.Services;

/// <summary>
/// Builds and caches a process-wide <see cref="TokenCredential"/> for Microsoft Entra auth.
/// Windows Authentication Manager reuses the signed-in Windows account when possible; the account
/// record and OS-protected MSAL cache keep subsequent app launches silent.
/// </summary>
/// <remarks>
/// A single coordinated credential is reused for the whole process. Silent token lookup and the
/// one explicit broker fallback are single-flight, so startup pre-warm and dictation cannot open
/// competing sign-in windows.
/// We do NOT chain AzureCliCredential: when `az` is signed in to another tenant it throws a
/// hard auth error (not "unavailable"), aborting a chain before the interactive fallback runs.
/// </remarks>
public static class EntraCredentialFactory
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceInput");
    internal const string CacheName = "VoiceInput.EntraCache";

    private static readonly object _lock = new();
    private static readonly Dictionary<string, SingleFlightTokenCredential> _cache = new();
    private static readonly Dictionary<string, long> _credentialGenerations = new();
    private static readonly RecoverableSemaphoreGate _interactiveGate = new();
    private static nint _parentWindowHandle;

    /// <summary>Sets the WPF owner used by the Windows authentication broker.</summary>
    public static void SetParentWindowHandle(nint handle)
    {
        if (handle == 0) return;
        lock (_lock) { _parentWindowHandle = handle; }
    }

    /// <summary>Returns the shared credential for the given tenant (created on first use).</summary>
    public static TokenCredential Create(string? tenantId) => GetOrCreate(Norm(tenantId));

    /// <summary>
    /// Opens the Windows account picker and atomically makes the selected account the saved account
    /// for this tenant. A cancelled/failed picker leaves the current account untouched.
    /// </summary>
    public static async Task<string> SwitchAccountAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        string tenant = Norm(tenantId);
        nint parentWindowHandle;
        lock (_lock) { parentWindowHandle = _parentWindowHandle; }

        var picker = new InteractiveBrowserCredential(CreateOptions(
            tenant,
            record: null,
            parentWindowHandle,
            useDefaultBrokerAccount: false));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string> authentication = SelectAndCommitAccountAsync(
            picker,
            tenant,
            parentWindowHandle,
            linkedCancellation.Token);

        try
        {
            return await authentication.WaitAsync(
                SingleFlightTokenCredential.DefaultAuthenticationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            linkedCancellation.Cancel();
            ObserveDetached(authentication);
            throw new TimeoutException("Microsoft Entra account selection did not finish within two minutes.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            linkedCancellation.Cancel();
            ObserveDetached(authentication);
            throw;
        }
    }

    /// <summary>
    /// Refreshes a cached WAM token without opening authentication UI. Returns false when an
    /// explicit account or Conditional Access interaction is required.
    /// </summary>
    public static async Task<bool> TryPrewarmAsync(
        string? tenantId,
        string scope,
        CancellationToken ct = default)
    {
        var cred = GetOrCreate(Norm(tenantId));
        try
        {
            await cred.GetTokenSilentlyAsync(new TokenRequestContext(new[] { scope }), ct);
            return true;
        }
        catch (AuthenticationRequiredException)
        {
            return false;
        }
    }

    private static SingleFlightTokenCredential GetOrCreate(string tenant)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(tenant, out var existing)) return existing;
            AuthenticationRecord? record = TryLoadRecord(tenant);
            long generation = NextGenerationLocked(tenant);
            var coordinated = CreateCoordinatedCredential(
                tenant,
                record,
                _parentWindowHandle,
                generation);
            _credentialGenerations[tenant] = generation;
            _cache[tenant] = coordinated;
            return coordinated;
        }
    }

    private static SingleFlightTokenCredential CreateCoordinatedCredential(
        string tenant,
        AuthenticationRecord? record,
        nint parentWindowHandle,
        long generation)
    {
        var interactive = new InteractiveBrowserCredential(CreateOptions(
            tenant,
            record,
            parentWindowHandle));
        return new SingleFlightTokenCredential(
            interactive,
            async (context, cancellationToken) =>
            {
                // WAM supports only one account dialog at a time. This process-wide gate also
                // covers a settings change that switches tenants while an older request ends.
                using (await _interactiveGate.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    // A Settings account switch may have replaced this credential while its caller
                    // waited for WAM. A stale generation must neither show UI nor overwrite the new
                    // AuthenticationRecord when it eventually resumes.
                    if (!IsCurrentGeneration(tenant, generation))
                        throw new InvalidOperationException(
                            "The Microsoft Entra account changed. Start a new dictation and try again.");

                    // Another tenant/context may have completed WAM while this request waited
                    // for the process-wide UI gate. Recheck silently before showing anything.
                    try
                    {
                        await interactive.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (AuthenticationRequiredException)
                    {
                    }

                    AuthenticationRecord updated = await interactive.AuthenticateAsync(
                        context,
                        cancellationToken).ConfigureAwait(false);
                    TrySaveRecordIfCurrent(tenant, generation, updated);
                }
            });
    }

    private static async Task<string> SelectAndCommitAccountAsync(
        InteractiveBrowserCredential picker,
        string tenant,
        nint parentWindowHandle,
        CancellationToken cancellationToken)
    {
        using (await _interactiveGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            AuthenticationRecord selected = await picker.AuthenticateAsync(
                new TokenRequestContext(new[] { CognitiveServicesScope }),
                cancellationToken).ConfigureAwait(false);
            // A timed-out broker can ignore cancellation and still return much later. Do not let
            // that detached result replace the saved account after the caller was told it failed.
            cancellationToken.ThrowIfCancellationRequested();
            CommitSwitchedAccount(tenant, parentWindowHandle, selected);
            return string.IsNullOrWhiteSpace(selected.Username) ? "selected account" : selected.Username;
        }
    }

    private static void CommitSwitchedAccount(
        string tenant,
        nint parentWindowHandle,
        AuthenticationRecord selected)
    {
        lock (_lock)
        {
            bool committed = TryCommitCredentialReplacement(
                _credentialGenerations,
                _cache,
                tenant,
                generation => CreateCoordinatedCredential(
                    tenant,
                    selected,
                    parentWindowHandle,
                    generation),
                () => TrySaveRecord(tenant, selected));
            if (!committed)
            {
                throw new IOException(
                    "The selected Microsoft Entra account could not be saved; the previous account was kept.");
            }
        }
    }

    internal static bool TryCommitCredentialReplacement<TCredential>(
        IDictionary<string, long> generations,
        IDictionary<string, TCredential> credentials,
        string tenant,
        Func<long, TCredential> createReplacement,
        Func<bool> persist)
    {
        long generation = generations.TryGetValue(tenant, out long current) ? current + 1 : 1;
        TCredential replacement = createReplacement(generation);
        if (!persist()) return false;
        generations[tenant] = generation;
        credentials[tenant] = replacement;
        return true;
    }

    private static long NextGenerationLocked(string tenant) =>
        _credentialGenerations.TryGetValue(tenant, out long current) ? current + 1 : 1;

    private static bool IsCurrentGeneration(string tenant, long generation)
    {
        lock (_lock)
        {
            return IsCurrentGeneration(_credentialGenerations, tenant, generation);
        }
    }

    internal static bool IsCurrentGeneration(
        IReadOnlyDictionary<string, long> generations,
        string tenant,
        long generation) =>
        generations.TryGetValue(tenant, out long current) && current == generation;

    internal static bool TryRunForCurrentGeneration(
        IReadOnlyDictionary<string, long> generations,
        string tenant,
        long generation,
        Func<bool> action) =>
        IsCurrentGeneration(generations, tenant, generation) && action();

    private static void TrySaveRecordIfCurrent(
        string tenant,
        long generation,
        AuthenticationRecord record)
    {
        lock (_lock)
        {
            _ = TryRunForCurrentGeneration(
                _credentialGenerations,
                tenant,
                generation,
                () => TrySaveRecord(tenant, record));
        }
    }

    private static void ObserveDetached(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted) _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static InteractiveBrowserCredentialBrokerOptions CreateOptions(
        string tenant,
        AuthenticationRecord? record,
        nint parentWindowHandle,
        bool? useDefaultBrokerAccount = null) =>
        new(parentWindowHandle)
        {
            TenantId = string.IsNullOrEmpty(tenant) ? null : tenant,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthenticationRecord = record,
            // A saved record wins on later launches. On first use, prefer the Windows work/school
            // account so a managed PC normally needs no account-picker click at all.
            UseDefaultBrokerAccount = useDefaultBrokerAccount ?? (record is null),
            // Do not set LoginHint: Azure.Identity maps it to Prompt.NoPrompt, which prevents WAM
            // from showing the one recovery dialog required for MFA, CA, or a stale account.
            LoginHint = null,
            // Service calls may only try the cache. The coordinator below owns the sole explicit
            // AuthenticateAsync fallback, preventing hidden or duplicate interactive prompts.
            DisableAutomaticAuthentication = true,
        };

    private static string Norm(string? tenantId) => string.IsNullOrWhiteSpace(tenantId) ? "" : tenantId.Trim();

    private static string RecordPath(string tenant) =>
        Path.Combine(CacheDir, $"entra-record-{(tenant.Length == 0 ? "default" : tenant)}.bin");

    private static AuthenticationRecord? TryLoadRecord(string tenant)
        => TryLoadRecordFile(RecordPath(tenant));

    internal static AuthenticationRecord? TryLoadRecordFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return AuthenticationRecord.Deserialize(fs);
        }
        catch (Exception ex)
        {
            Log.Write($"Entra authentication record ignored ({ex.GetType().Name}); it will be replaced after sign-in.");
            return null;
        }
    }

    private static bool TrySaveRecord(string tenant, AuthenticationRecord record)
        => TrySaveRecordFile(RecordPath(tenant), record);

    internal static bool TrySaveRecordFile(string path, AuthenticationRecord record)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    record.Serialize(fs);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Entra authentication record could not be saved ({ex.GetType().Name}); OS token caching remains active.");
            return false;
        }
    }
}

/// <summary>
/// Lets every token request perform its own context-correct silent lookup while coalescing the
/// explicit interactive step. A caller can stop waiting without cancelling WAM while another
/// caller still needs it; when the final waiter leaves, the shared interaction is cancelled.
/// </summary>
internal sealed class SingleFlightTokenCredential : TokenCredential
{
    internal static readonly TimeSpan DefaultAuthenticationTimeout = TimeSpan.FromMinutes(2);

    private readonly TokenCredential _silentCredential;
    private readonly Func<TokenRequestContext, CancellationToken, Task> _authenticateAsync;
    private readonly TimeSpan _authenticationTimeout;
    private readonly object _lock = new();
    private AuthenticationFlight? _authenticationFlight;

    internal SingleFlightTokenCredential(
        TokenCredential silentCredential,
        Func<TokenRequestContext, CancellationToken, Task> authenticateAsync,
        TimeSpan? authenticationTimeout = null)
    {
        _silentCredential = silentCredential;
        _authenticateAsync = authenticateAsync;
        _authenticationTimeout = authenticationTimeout ?? DefaultAuthenticationTimeout;
        if (_authenticationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(authenticationTimeout));
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        new(AcquireTokenAsync(requestContext, cancellationToken));

    internal ValueTask<AccessToken> GetTokenSilentlyAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        _silentCredential.GetTokenAsync(requestContext, cancellationToken);

    private async Task<AccessToken> AcquireTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthenticationRequestKey requestKey = AuthenticationRequestKey.From(requestContext);
        bool authenticatedForThisContext = false;

        while (true)
        {
            try
            {
                return await GetTokenSilentlyAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationRequiredException) when (!authenticatedForThisContext)
            {
            }

            AuthenticationFlight flight = JoinOrStartAuthentication(requestContext, requestKey);
            bool coversThisContext = flight.Key == requestKey;
            Exception? authenticationFailure = null;
            try
            {
                await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!coversThisContext && !cancellationToken.IsCancellationRequested)
            {
                // A serialized interaction for another context was cancelled. This caller still
                // gets to retry its own context once that WAM window has finished closing.
            }
            catch (Exception) when (!coversThisContext)
            {
                // Failure to authenticate scope/claims A must not poison an unrelated context B.
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
            {
                // Some WAM failures happen during teardown after MSAL has already cached the token.
                // Give that token exactly one silent chance; never open another dialog here.
                authenticationFailure = ex;
            }
            finally
            {
                ReleaseAuthentication(flight);
            }

            if (authenticationFailure is not null)
            {
                try
                {
                    return await GetTokenSilentlyAsync(requestContext, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExceptionDispatchInfo.Capture(authenticationFailure).Throw();
                    throw;
                }
            }

            // A caller with different scopes/claims may have joined the current dialog solely to
            // avoid two simultaneous WAM windows. Retry its own silent request; if that context
            // still needs interaction, it gets one subsequent flight rather than the wrong token.
            authenticatedForThisContext |= coversThisContext;
        }
    }

    private AuthenticationFlight JoinOrStartAuthentication(
        TokenRequestContext requestContext,
        AuthenticationRequestKey requestKey)
    {
        lock (_lock)
        {
            AuthenticationFlight? flight = _authenticationFlight;
            if (flight is { } && (flight.Completed || flight.Task.IsCompleted))
                flight = null;
            if (flight is null)
            {
                var cancellation = new CancellationTokenSource();
                Task task = AuthenticateCoreAsync(requestContext, cancellation);
                flight = new AuthenticationFlight(requestKey, cancellation, task);
                _authenticationFlight = flight;
                _ = task.ContinueWith(
                    static (_, state) =>
                    {
                        var completion = ((SingleFlightTokenCredential Owner, AuthenticationFlight Flight))state!;
                        completion.Owner.CompleteAuthentication(completion.Flight);
                    },
                    (this, flight),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            flight.Waiters++;
            return flight;
        }
    }

    private async Task AuthenticateCoreAsync(
        TokenRequestContext requestContext,
        CancellationTokenSource cancellation)
    {
        // Never invoke broker code while holding _lock; AuthenticateAsync can complete or cancel
        // synchronously on some WAM error paths.
        await Task.Yield();
        Task authentication = _authenticateAsync(requestContext, cancellation.Token);
        try
        {
            // Cancellation is advisory for some broker/OS failure modes. WaitAsync provides the
            // hard boundary: callers and later authentication flights recover even if WAM ignores
            // its cancellation token forever.
            await authentication.WaitAsync(_authenticationTimeout, cancellation.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            ObserveDetachedAuthentication(authentication);
            throw new TimeoutException("Microsoft Entra authentication did not finish within two minutes.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ObserveDetachedAuthentication(authentication);
            throw;
        }
    }

    private static void ObserveDetachedAuthentication(Task authentication)
    {
        if (authentication.IsCompleted)
        {
            if (authentication.IsFaulted) _ = authentication.Exception;
            return;
        }

        _ = authentication.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteAuthentication(AuthenticationFlight flight)
    {
        bool dispose;
        lock (_lock)
        {
            flight.Completed = true;
            if (ReferenceEquals(_authenticationFlight, flight))
                _authenticationFlight = null;
            dispose = TryMarkForDisposal(flight);
        }
        if (dispose) flight.Cancellation.Dispose();
    }

    private void ReleaseAuthentication(AuthenticationFlight flight)
    {
        bool cancel = false;
        bool dispose;
        lock (_lock)
        {
            flight.Waiters--;
            if (flight.Waiters == 0 && !flight.Completed)
            {
                flight.CancelPending = true;
                if (ReferenceEquals(_authenticationFlight, flight))
                    _authenticationFlight = null;
                cancel = true;
            }
            dispose = TryMarkForDisposal(flight);
        }

        if (cancel)
        {
            try
            {
                flight.Cancellation.Cancel();
            }
            catch (Exception ex)
            {
                Log.Write($"Entra shared authentication cancellation failed ({ex.GetType().Name}).");
            }
            finally
            {
                lock (_lock)
                {
                    flight.CancelPending = false;
                    dispose |= TryMarkForDisposal(flight);
                }
            }
        }

        if (dispose) flight.Cancellation.Dispose();
    }

    private static bool TryMarkForDisposal(AuthenticationFlight flight)
    {
        if (!flight.Completed || flight.Waiters != 0 || flight.CancelPending || flight.Disposed)
            return false;
        flight.Disposed = true;
        return true;
    }

    private sealed class AuthenticationFlight(
        AuthenticationRequestKey key,
        CancellationTokenSource cancellation,
        Task task)
    {
        internal AuthenticationRequestKey Key { get; } = key;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task Task { get; } = task;
        internal int Waiters { get; set; }
        internal bool Completed { get; set; }
        internal bool CancelPending { get; set; }
        internal bool Disposed { get; set; }
    }

    private readonly record struct AuthenticationRequestKey(
        string Scopes,
        string? Claims,
        string? TenantId,
        bool IsCaeEnabled,
        bool IsProofOfPossessionEnabled,
        string? ProofOfPossessionNonce,
        string? ResourceRequestUri,
        string? ResourceRequestMethod)
    {
        internal static AuthenticationRequestKey From(TokenRequestContext context) => new(
            string.Join("\u001F", context.Scopes.OrderBy(static scope => scope, StringComparer.Ordinal)),
            context.Claims,
            context.TenantId,
            context.IsCaeEnabled,
            context.IsProofOfPossessionEnabled,
            context.ProofOfPossessionNonce,
            context.ResourceRequestUri?.AbsoluteUri,
            context.ResourceRequestMethod);
    }
}

/// <summary>
/// Serializes WAM UI while keeping a cancelled broker call from overlapping a new dialog. Callers
/// fail fast while an unresponsive WAM task is still closing; once that task actually exits, the
/// lease is released and a later authentication attempt can recover normally.
/// </summary>
internal sealed class RecoverableSemaphoreGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _lock = new();
    private TaskCompletionSource _stateChanged = NewSignal();
    private Lease? _owner;
    private bool _isUnavailable;
    private long _cancellationGeneration;

    internal async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        long cancellationGeneration;
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isUnavailable)
                throw PreviousBrokerStillRunning();
            cancellationGeneration = _cancellationGeneration;
        }

        while (true)
        {
            Task stateChanged;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isUnavailable || _cancellationGeneration != cancellationGeneration)
                    throw PreviousBrokerStillRunning();
                stateChanged = _stateChanged.Task;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task wait = _semaphore.WaitAsync(linkedCancellation.Token);
            Task completed = await Task.WhenAny(wait, stateChanged).ConfigureAwait(false);
            if (!ReferenceEquals(completed, wait))
            {
                linkedCancellation.Cancel();
                bool acquired = false;
                try
                {
                    await wait.ConfigureAwait(false);
                    acquired = true;
                }
                catch (OperationCanceledException) { }
                // A release can race the state-change notification and let this wait own the
                // permit before cancellation arrives. Return it before retrying or failing.
                if (acquired) _semaphore.Release();
                cancellationToken.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    if (_isUnavailable || _cancellationGeneration != cancellationGeneration)
                        throw PreviousBrokerStillRunning();
                }
                // The owner exited normally and another waiter may now own the gate. Subscribe to
                // that owner's state so its later cancellation also wakes this queued caller.
                continue;
            }

            await wait.ConfigureAwait(false);
            var lease = new Lease(this);
            bool accepted;
            lock (_lock)
            {
                accepted = !_isUnavailable
                    && _cancellationGeneration == cancellationGeneration
                    && _owner is null;
                if (accepted) _owner = lease;
            }
            if (!accepted)
            {
                _semaphore.Release();
                cancellationToken.ThrowIfCancellationRequested();
                throw PreviousBrokerStillRunning();
            }

            try
            {
                lease.RegisterCancellation(cancellationToken);
                return lease;
            }
            catch
            {
                Release(lease);
                throw;
            }
        }
    }

    private void MarkUnavailable(Lease lease)
    {
        TaskCompletionSource? signal = null;
        lock (_lock)
        {
            if (ReferenceEquals(_owner, lease) && !_isUnavailable)
            {
                _isUnavailable = true;
                _cancellationGeneration++;
                signal = _stateChanged;
            }
        }
        signal?.TrySetResult();
    }

    private void Release(Lease lease)
    {
        bool release = false;
        TaskCompletionSource? signal = null;
        lock (_lock)
        {
            if (ReferenceEquals(_owner, lease))
            {
                _owner = null;
                _isUnavailable = false;
                signal = _stateChanged;
                _stateChanged = NewSignal();
                release = true;
            }
        }
        if (release)
        {
            _semaphore.Release();
            signal!.TrySetResult();
        }
    }

    private static InvalidOperationException PreviousBrokerStillRunning() => new(
        "A previous Microsoft Entra sign-in is still closing. Try again in a moment.");

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Lease : IDisposable
    {
        private readonly RecoverableSemaphoreGate _owner;
        private CancellationTokenRegistration _cancellationRegistration;
        private int _released;

        internal Lease(RecoverableSemaphoreGate owner)
        {
            _owner = owner;
        }

        internal void RegisterCancellation(CancellationToken cancellationToken)
        {
            _cancellationRegistration = cancellationToken.UnsafeRegister(
                static state => ((Lease)state!).OnCancellation(),
                this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(this);
            _cancellationRegistration.Dispose();
        }

        private void OnCancellation()
        {
            if (Volatile.Read(ref _released) == 0)
                _owner.MarkUnavailable(this);
        }
    }
}
