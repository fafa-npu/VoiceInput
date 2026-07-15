using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class FunAsrLiveTests
{
    [LiveFunAsrFact]
    [Trait("Category", "Live")]
    public async Task InstallsAndSmokeTestsDefaultModelWhenEnabled()
    {
        using var manager = new FunAsrRuntimeManager();

        await manager.InstallAsync(FunAsrModelCatalog.DefaultId, CancellationToken.None);

        Assert.True(manager.IsInstalled(FunAsrModelCatalog.DefaultId));
        FunAsrResolvedModel resolved = manager.Resolve(FunAsrModelCatalog.DefaultId);
        Assert.True(File.Exists(resolved.ExecutablePath));
        Assert.True(File.Exists(resolved.VadPath));
        Assert.All(resolved.ArtifactPaths.Values, path => Assert.True(File.Exists(path)));
    }
}

public sealed class LiveFunAsrFactAttribute : FactAttribute
{
    public LiveFunAsrFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOICEINPUT_RUN_LIVE_FUNASR") != "1")
        {
            Skip = "Set VOICEINPUT_RUN_LIVE_FUNASR=1 to run the network and native-runtime smoke test.";
        }
    }
}
