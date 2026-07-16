using System.Text.Json;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshSettingsRequireOnboardingAndDefaultToLocalFunAsr()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));

        Assert.False(store.Exists);
        AppSettings settings = store.Load();
        Assert.False(settings.OnboardingCompleted);
        Assert.Equal(SpeechEngineKind.FunAsr, settings.Engine);
        Assert.Equal(FunAsrModelCatalog.DefaultId, settings.FunAsrModelId);
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
            PttMode = PttMode.Toggle,
            Engine = SpeechEngineKind.FunAsr,
            FunAsrModelId = "paraformer-zh-q8",
            LlmEnabled = true,
            DiagnosticLogging = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();
        using JsonDocument savedJson = JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(store.Exists);
        Assert.Equal("Toggle", savedJson.RootElement.GetProperty("PttMode").GetString());
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal("en-US", loaded.Language);
        Assert.Equal("CapsLock", loaded.PttKey);
        Assert.Equal(PttMode.Toggle, loaded.PttMode);
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
        Assert.Equal(PttMode.Hold, loaded.PttMode);
        Assert.True(loaded.OnboardingCompleted);
    }

    [Fact]
    public void LegacySettingsWithoutEngineKeepWindowsDefault()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {
              "Language": "en-US"
            }
            """);
        var store = new SettingsStore(path);

        AppSettings loaded = store.Load();

        Assert.Equal(SpeechEngineKind.Windows, loaded.Engine);
        Assert.True(loaded.OnboardingCompleted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
