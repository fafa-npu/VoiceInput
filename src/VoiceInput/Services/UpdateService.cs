using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace VoiceInput.Services;

/// <summary>
/// Manual, user-initiated update check against the GitHub Enterprise Releases for this repo.
/// Talks to the GHE REST API directly (the bundled `gh` is only used to obtain a token, since
/// older gh builds mishandle this instance's release asset endpoints). Never updates automatically.
/// Prerequisite: `gh auth login --hostname &lt;GheHost&gt;` once, plus a published release with the exe asset.
/// </summary>
public sealed class UpdateService
{
    public const string GheHost = "github.com";
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

    public enum CheckOutcome { UpToDate, UpdateAvailable, CheckFailed }

    public sealed record CheckResult(CheckOutcome Outcome, string? LatestTag, Version? Latest, string? AssetApiUrl);

    public async Task<CheckResult> CheckAsync()
    {
        string? token = await GetGheTokenAsync();
        if (token is null) return new CheckResult(CheckOutcome.CheckFailed, null, null, null);

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
        string? token = await GetGheTokenAsync();
        if (token is null) return false;

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "VoiceInputUpdate");
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

            string target = Environment.ProcessPath!;
            int pid = Environment.ProcessId;
            string ps = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600; " +
                        $"Copy-Item -LiteralPath '{newExe}' -Destination '{target}' -Force; " +
                        $"Start-Process -FilePath '{target}'";
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddHeaders(HttpRequestMessage req, string token, string accept)
    {
        req.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        req.Headers.TryAddWithoutValidation("Accept", accept);
        req.Headers.TryAddWithoutValidation("User-Agent", "VoiceInput-Updater");
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build) : null;
    }

    /// <summary>Reuses the user's existing GHE login via `gh auth token`; returns null if unavailable.</summary>
    private static async Task<string?> GetGheTokenAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", $"auth token --hostname {GheHost}")
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
