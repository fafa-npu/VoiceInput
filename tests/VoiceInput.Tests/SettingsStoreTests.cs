using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripsNonSecretSettings()
    {
        string path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings
        {
            Language = "en-US",
            PttKey = "CapsLock",
            LlmEnabled = true,
            DiagnosticLogging = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.True(store.Exists);
        Assert.Equal("en-US", loaded.Language);
        Assert.Equal("CapsLock", loaded.PttKey);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
