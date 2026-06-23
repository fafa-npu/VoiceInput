using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VoiceInput.Services;

/// <summary>
/// Manual, user-initiated update check against the GitHub Enterprise Releases for this repo.
/// Uses the `gh` CLI as the backend so it reuses the user's existing GHE auth (no token to manage).
/// Never updates automatically — callers decide whether to apply.
/// Prerequisite: `gh auth login --hostname &lt;GheHost&gt;` once, and at least one published release.
/// </summary>
public sealed class UpdateService
{
    public const string GheHost = "microsoft.ghe.com";
    public const string Repo = "Zhao-Hua/VoiceInput";

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
        }
    }

    public enum CheckOutcome { UpToDate, UpdateAvailable, CheckFailed }

    public sealed record CheckResult(CheckOutcome Outcome, string? LatestTag, Version? Latest);

    public async Task<CheckResult> CheckAsync()
    {
        var (ok, output) = await RunGhAsync($"release view --repo {Repo} --json tagName");
        if (!ok) return new CheckResult(CheckOutcome.CheckFailed, null, null);

        string? tag;
        try
        {
            using var doc = JsonDocument.Parse(output);
            tag = doc.RootElement.GetProperty("tagName").GetString();
        }
        catch { return new CheckResult(CheckOutcome.CheckFailed, null, null); }

        var latest = ParseVersion(tag);
        if (latest is null) return new CheckResult(CheckOutcome.CheckFailed, tag, null);

        return latest > CurrentVersion
            ? new CheckResult(CheckOutcome.UpdateAvailable, tag, latest)
            : new CheckResult(CheckOutcome.UpToDate, tag, latest);
    }

    /// <summary>
    /// Downloads the release exe and launches a detached helper that waits for this process to exit,
    /// replaces the running exe in place, and relaunches it. Returns false if the download failed
    /// (in which case nothing was changed and the app keeps running).
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "VoiceInputUpdate");
        Directory.CreateDirectory(dir);

        var (ok, _) = await RunGhAsync(
            $"release download {tag} --repo {Repo} --pattern VoiceInput.exe --dir \"{dir}\" --clobber");
        string newExe = Path.Combine(dir, "VoiceInput.exe");
        if (!ok || !File.Exists(newExe)) return false;

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

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build) : null;
    }

    private static async Task<(bool ok, string output)> RunGhAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["GH_HOST"] = GheHost;
            using var p = Process.Start(psi);
            if (p is null) return (false, string.Empty);
            string outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, outp);
        }
        catch
        {
            return (false, string.Empty);   // gh not installed / not authed -> treated as check failure
        }
    }
}
