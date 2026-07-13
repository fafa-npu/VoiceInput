using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace VoiceInput.Services;

/// <summary>
/// Manual, user-initiated update check against the public GitHub Releases for this repo.
/// Works anonymously (public repo); if a `gh` login for github.com is present its token is used
/// for a higher rate limit, but it is never required. Never updates automatically.
/// </summary>
public sealed class UpdateService
{
    public const string Host = "github.com";
    public const string Repo = "fafa-npu/VoiceInput";
    private const string AssetName = "VoiceInput.exe";
    private static readonly string ApiBase = "https://api.github.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
        }
    }

    public enum CheckOutcome { UpToDate, UpdateAvailable, UpdatesDisabled, CheckFailed }

    public sealed record CheckResult(CheckOutcome Outcome, string? LatestTag, Version? Latest, string? AssetApiUrl);

    public static bool UpdatesEnabled =>
        !string.IsNullOrWhiteSpace(AuthenticodeVerifier.ExpectedCertificateSha256);

    public async Task<CheckResult> CheckAsync()
    {
        // Unsigned development builds intentionally have no publisher pin and must never offer updates.
        if (!UpdatesEnabled)
            return new CheckResult(CheckOutcome.UpdatesDisabled, null, null, null);
        string? token = await GetGitHubTokenAsync();   // optional: null ⇒ anonymous (public repo)

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{Repo}/releases/latest");
            AddHeaders(req, token, "application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new CheckResult(CheckOutcome.CheckFailed, null, null, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string? tag = root.GetProperty("tag_name").GetString();
            var latest = ParseVersion(tag);
            if (latest is null) return new CheckResult(CheckOutcome.CheckFailed, tag, null, null);

            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.GetProperty("name").GetString() == AssetName)
                    {
                        assetUrl = a.GetProperty("url").GetString();
                        break;
                    }
                }
            }

            var outcome = latest > CurrentVersion ? CheckOutcome.UpdateAvailable : CheckOutcome.UpToDate;
            return new CheckResult(outcome, tag, latest, assetUrl);
        }
        catch
        {
            return new CheckResult(CheckOutcome.CheckFailed, null, null, null);
        }
    }

    /// <summary>
    /// Downloads the release exe (authenticated REST) and launches a detached helper that waits for
    /// this process to exit, replaces the running exe in place, and relaunches it. Returns false on
    /// any failure (nothing is changed and the app keeps running).
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(string assetApiUrl)
    {
        string? token = await GetGitHubTokenAsync();   // optional: null ⇒ anonymous (public repo)

        try
        {
            // Isolate concurrent attempts and abandoned downloads from one another.
            string dir = Path.Combine(Path.GetTempPath(), "VoiceInputUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string newExe = Path.Combine(dir, AssetName);

            using (var req = new HttpRequestMessage(HttpMethod.Get, assetApiUrl))
            {
                // Accept octet-stream -> the API 302-redirects to the signed blob; HttpClient follows
                // and drops the Authorization header cross-host, which the signed URL expects.
                AddHeaders(req, token, "application/octet-stream");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return false;
                await using var fs = File.Create(newExe);
                await resp.Content.CopyToAsync(fs);
            }
            if (new FileInfo(newExe).Length < 1_000_000) return false;   // sanity: real exe is ~80 MB
            if (!AuthenticodeVerifier.VerifyPinnedPublisher(newExe))
            {
                Directory.Delete(dir, recursive: true);
                return false;
            }

            string target = Environment.ProcessPath!;
            int pid = Environment.ProcessId;
            string helper = Path.Combine(dir, "apply-update.ps1");
            await File.WriteAllTextAsync(helper, UpdateHelperScript);
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(helper);
            psi.ArgumentList.Add("-NewExe");
            psi.ArgumentList.Add(newExe);
            psi.ArgumentList.Add("-Target");
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add("-ProcessId");
            psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Process.Start(psi) is null)
            {
                Log.Write("Update helper could not be started.");
                Directory.Delete(dir, recursive: true);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const string UpdateHelperScript = """
        param(
            [Parameter(Mandatory=$true)][string]$NewExe,
            [Parameter(Mandatory=$true)][string]$Target,
            [Parameter(Mandatory=$true)][int]$ProcessId
        )
        $ErrorActionPreference = 'Stop'
        $tempTarget = "$Target.new"
        $backup = "$Target.backup"
        Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600
        try {
            Copy-Item -LiteralPath $NewExe -Destination $tempTarget -Force
            if ((Get-Item -LiteralPath $tempTarget).Length -lt 1000000) {
                throw 'Downloaded executable is incomplete.'
            }
            if (Test-Path -LiteralPath $Target) {
                [System.IO.File]::Replace($tempTarget, $Target, $backup, $true)
            } else {
                [System.IO.File]::Move($tempTarget, $Target)
            }
            $process = Start-Process -FilePath $Target -PassThru
            Start-Sleep -Seconds 3
            if ($process.HasExited) {
                throw "Updated application exited during startup ($($process.ExitCode))."
            }
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        } catch {
            if (Test-Path -LiteralPath $backup) {
                if (Test-Path -LiteralPath $Target) {
                    [System.IO.File]::Replace($backup, $Target, $null, $true)
                } else {
                    [System.IO.File]::Move($backup, $Target)
                }
                Start-Process -FilePath $Target
            }
        } finally {
            Remove-Item -LiteralPath $tempTarget -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $NewExe -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;

    private static void AddHeaders(HttpRequestMessage req, string? token, string accept)
    {
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("Accept", accept);
        req.Headers.TryAddWithoutValidation("User-Agent", "VoiceInput-Updater");
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build) : null;
    }

    /// <summary>Reuses an existing github.com `gh` login for a higher rate limit; null if unavailable
    /// (the public Releases API works fine anonymously).</summary>
    private static async Task<string?> GetGitHubTokenAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", $"auth token --hostname {Host}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0 && outp.Length > 0) ? outp : null;
        }
        catch
        {
            return null;
        }
    }
}
