using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task UnsignedDevelopmentBuildReportsUpdatesDisabled()
    {
        Assert.True(string.IsNullOrWhiteSpace(AuthenticodeVerifier.ExpectedCertificateSha256));

        var result = await new UpdateService().CheckAsync();

        Assert.Equal(UpdateService.CheckOutcome.UpdatesDisabled, result.Outcome);
    }
}
