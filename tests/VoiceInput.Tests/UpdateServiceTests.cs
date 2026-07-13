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
}
