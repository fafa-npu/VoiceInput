using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshSettingsRequireOnboarding()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));

        Assert.False(store.Exists);
        Assert.False(store.Load().OnboardingCompleted);
    }

    [Fact]
    public void RoundTripsNonSecretSettings()
    {
        string path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings
        {
            OnboardingCompleted = true,
            Language = "en-US",
            PttKey = "CapsLock",
            Engine = SpeechEngineKind.FunAsr,
            FunAsrModelId = "paraformer-zh-q8",
            LlmEnabled = true,
            DiagnosticLogging = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.True(store.Exists);
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal("en-US", loaded.Language);
        Assert.Equal("CapsLock", loaded.PttKey);
        Assert.Equal(SpeechEngineKind.FunAsr, loaded.Engine);
        Assert.Equal("paraformer-zh-q8", loaded.FunAsrModelId);
        Assert.True(loaded.LlmEnabled);
        Assert.True(loaded.DiagnosticLogging);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RemovesTemporaryFileWhenCommitFails()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(path);
        var store = new SettingsStore(path);

        Assert.ThrowsAny<IOException>(() => store.Save(new AppSettings()));

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void OldSettingsKeepExistingEngineDefaultsAndCompletedOnboarding()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {
              "Language": "en-US",
              "Engine": "GptTranscribe"
            }
            """);
        var store = new SettingsStore(path);

        AppSettings loaded = store.Load();

        Assert.Equal(SpeechEngineKind.GptTranscribe, loaded.Engine);
        Assert.Equal(FunAsrModelCatalog.DefaultId, loaded.FunAsrModelId);
        Assert.True(loaded.OnboardingCompleted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
