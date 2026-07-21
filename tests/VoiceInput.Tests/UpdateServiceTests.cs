using System.Security.Cryptography;
using System.Text;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void UnsignedDevelopmentBuildUsesReleaseDigestVerification() =>
        Assert.False(UpdateService.UsesPinnedPublisherVerification);

    [Fact]
    public void ParsesGitHubReleaseSha256Digest()
    {
        string hash = new('A', 64);

        Assert.Equal(hash, UpdateService.ParseSha256Digest($"sha256:{hash}"));
        Assert.Null(UpdateService.ParseSha256Digest($"md5:{hash}"));
    }

    [Fact]
    public void AcceptsOnlyMatchingReleaseSha256()
    {
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes("VoiceInput release"));

        Assert.True(UpdateService.MatchesSha256(actual, Convert.ToHexString(actual)));
        Assert.False(UpdateService.MatchesSha256(actual, new string('0', 64)));
    }

    [Fact]
    public async Task DownloadCopyReportsActualByteProgress()
    {
        byte[] payload = new byte[2_500_123];
        Random.Shared.NextBytes(payload);
        await using var source = new MemoryStream(payload, writable: false);
        await using var destination = new MemoryStream();
        var reports = new List<(long Received, long? Total)>();

        await UpdateService.CopyDownloadAsync(
            source,
            destination,
            payload.Length,
            (received, total) => reports.Add((received, total)));

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal((0, (long?)payload.Length), reports[0]);
        Assert.Equal(((long)payload.Length, (long?)payload.Length), reports[^1]);
        Assert.All(reports, report => Assert.Equal((long?)payload.Length, report.Total));
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.Received < pair.Second.Received));
        Assert.InRange(reports.Count, 2, 101);
    }

    [Fact]
    public void UpdateStatusCalculatesBoundedPercentage()
    {
        var status = new UpdateService.UpdateStatus(
            UpdateService.UpdateStage.Downloading,
            "Downloading...",
            "v9.9.9",
            45,
            100);

        Assert.Equal(45, status.Percentage);
        Assert.Equal(100, (status with { BytesReceived = 120 }).Percentage);
        Assert.Null((status with { TotalBytes = null }).Percentage);
    }
}
