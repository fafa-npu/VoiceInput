using System.Collections.Generic;
using System.IO;
using Azure.Core;
using Azure.Identity;

namespace VoiceInput.Services;

/// <summary>
/// Builds and caches a process-wide <see cref="TokenCredential"/> for Microsoft Entra auth.
/// Interactive browser sign-in is used once; the account record is persisted so subsequent
/// app launches authenticate silently (tokens live in the OS-protected MSAL cache).
/// </summary>
/// <remarks>
/// A single credential instance is reused for the whole process — creating a fresh one per
/// dictation re-prompts the browser every time because the in-memory token cache is lost.
/// We do NOT chain AzureCliCredential: when `az` is signed in to another tenant it throws a
/// hard auth error (not "unavailable"), aborting a chain before the interactive fallback runs.
/// </remarks>
public static class EntraCredentialFactory
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceInput");
    private const string CacheName = "VoiceInput.EntraCache";

    private static readonly object _lock = new();
    private static readonly Dictionary<string, InteractiveBrowserCredential> _cache = new();

    /// <summary>Returns the shared credential for the given tenant (created on first use).</summary>
    public static TokenCredential Create(string? tenantId) => GetOrCreate(Norm(tenantId));

    /// <summary>
    /// Sign in once up front so the browser popup never steals focus during dictation.
    /// Silent (no popup) when a saved account record exists; interactive only the first time.
    /// </summary>
    public static async Task PrewarmAsync(string? tenantId, string scope, CancellationToken ct = default)
    {
        var tenant = Norm(tenantId);
        var cred = GetOrCreate(tenant);
        var ctx = new TokenRequestContext(new[] { scope });

        if (HasRecord(tenant))
        {
            await cred.GetTokenAsync(ctx, ct);                    // silent via persisted record + cache
        }
        else
        {
            var record = await cred.AuthenticateAsync(ctx, ct);   // interactive, once
            TrySaveRecord(tenant, record);
        }
    }

    private static InteractiveBrowserCredential GetOrCreate(string tenant)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(tenant, out var existing)) return existing;
            var cred = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = string.IsNullOrEmpty(tenant) ? null : tenant,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
                AuthenticationRecord = TryLoadRecord(tenant),
            });
            _cache[tenant] = cred;
            return cred;
        }
    }

    private static string Norm(string? tenantId) => string.IsNullOrWhiteSpace(tenantId) ? "" : tenantId.Trim();

    private static string RecordPath(string tenant) =>
        Path.Combine(CacheDir, $"entra-record-{(tenant.Length == 0 ? "default" : tenant)}.bin");

    private static bool HasRecord(string tenant) => File.Exists(RecordPath(tenant));

    private static AuthenticationRecord? TryLoadRecord(string tenant)
    {
        try
        {
            var path = RecordPath(tenant);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return AuthenticationRecord.Deserialize(fs);
        }
        catch { return null; }
    }

    private static void TrySaveRecord(string tenant, AuthenticationRecord record)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            using var fs = File.Create(RecordPath(tenant));
            record.Serialize(fs);
        }
        catch { /* best effort — falls back to interactive next launch */ }
    }
}
