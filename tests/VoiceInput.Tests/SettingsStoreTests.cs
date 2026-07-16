using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal(TranscribeModelKind.Gpt4oTranscribe, settings.TranscribeModelKind);
        Assert.Empty(settings.RecognitionVocabulary);
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
            TranscribeModelKind = TranscribeModelKind.Gpt4oMiniTranscribe,
            RecognitionVocabulary = [" Jaws ", "", "jaws", "Daybreak"],
            LlmEnabled = true,
            DiagnosticLogging = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();
        using JsonDocument savedJson = JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(store.Exists);
        Assert.Equal("Toggle", savedJson.RootElement.GetProperty("PttMode").GetString());
        Assert.Equal("Gpt4oMiniTranscribe", savedJson.RootElement.GetProperty("TranscribeModelKind").GetString());
        Assert.Equal(
            ["Jaws", "Daybreak"],
            savedJson.RootElement.GetProperty("RecognitionVocabulary").EnumerateArray().Select(item => item.GetString()));
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal("en-US", loaded.Language);
        Assert.Equal("CapsLock", loaded.PttKey);
        Assert.Equal(PttMode.Toggle, loaded.PttMode);
        Assert.Equal(SpeechEngineKind.FunAsr, loaded.Engine);
        Assert.Equal("paraformer-zh-q8", loaded.FunAsrModelId);
        Assert.Equal(TranscribeModelKind.Gpt4oMiniTranscribe, loaded.TranscribeModelKind);
        Assert.Equal(["Jaws", "Daybreak"], loaded.RecognitionVocabulary);
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
        Assert.Equal(TranscribeModelKind.Unknown, loaded.TranscribeModelKind);
        Assert.Empty(loaded.RecognitionVocabulary);
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
        Assert.Equal(TranscribeModelKind.Unknown, loaded.TranscribeModelKind);
    }

    [Fact]
    public void UnknownModelKindFallsBackToUnknown()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {
              "Language": "en-US",
              "TranscribeModelKind": "future-model",
              "RecognitionVocabulary": ["Jaws"]
            }
            """);
        var store = new SettingsStore(path);

        AppSettings loaded = store.Load();

        Assert.Equal("en-US", loaded.Language);
        Assert.Equal(TranscribeModelKind.Unknown, loaded.TranscribeModelKind);
        Assert.Equal(["Jaws"], loaded.RecognitionVocabulary);
    }

    [Theory]
    [InlineData("null", "null", TranscribeModelKind.Unknown)]
    [InlineData("\"scalar\"", "42", TranscribeModelKind.Unknown)]
    [InlineData("{ \"term\": \"Jaws\" }", "{ \"kind\": \"Gpt4oTranscribe\" }", TranscribeModelKind.Unknown)]
    [InlineData("[\" Alpha \", 7, null, { \"term\": \"ignored\" }, \"alpha\", \"Beta\"]", "\"Gpt4oTranscribe\"", TranscribeModelKind.Gpt4oTranscribe)]
    public void InvalidNewFieldShapesDoNotDiscardOtherSettings(
        string vocabularyJson,
        string modelKindJson,
        TranscribeModelKind expectedModelKind)
    {
        string path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings
        {
            Language = "vi-VN",
            Engine = SpeechEngineKind.GptTranscribe,
            AzureAuthMode = AzureAuthMode.EntraId,
            AzureEndpoint = "https://speech.example.test/",
            AzureKey = "azure-secret",
            TranscribeAuthMode = AzureAuthMode.Key,
            TranscribeEndpoint = "https://transcribe.example.test/",
            TranscribeApiKey = "transcribe-secret",
            TranscribeTenantId = "tenant-id",
        });
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["RecognitionVocabulary"] = JsonNode.Parse(vocabularyJson);
        json["TranscribeModelKind"] = JsonNode.Parse(modelKindJson);
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        AppSettings loaded = store.Load();

        Assert.Equal("vi-VN", loaded.Language);
        Assert.Equal(SpeechEngineKind.GptTranscribe, loaded.Engine);
        Assert.Equal(AzureAuthMode.EntraId, loaded.AzureAuthMode);
        Assert.Equal("https://speech.example.test/", loaded.AzureEndpoint);
        Assert.Equal("azure-secret", loaded.AzureKey);
        Assert.Equal(AzureAuthMode.Key, loaded.TranscribeAuthMode);
        Assert.Equal("https://transcribe.example.test/", loaded.TranscribeEndpoint);
        Assert.Equal("transcribe-secret", loaded.TranscribeApiKey);
        Assert.Equal("tenant-id", loaded.TranscribeTenantId);
        Assert.Equal(expectedModelKind, loaded.TranscribeModelKind);
        Assert.Equal(
            vocabularyJson.StartsWith("[", StringComparison.Ordinal) ? ["Alpha", "Beta"] : [],
            loaded.RecognitionVocabulary);
    }

    [Fact]
    public void LoadKeepsLargeVocabulary()
    {
        string path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        string[] entries = Enumerable.Range(1, 250)
            .Select(index => $"Term {index}")
            .ToArray();
        store.Save(new AppSettings
        {
            TranscribeModelKind = TranscribeModelKind.Gpt4oTranscribe,
            RecognitionVocabulary = entries,
        });

        AppSettings loaded = store.Load();

        Assert.Equal(entries, loaded.RecognitionVocabulary);
    }

    [Fact]
    public void CorruptSettingsReturnSafeDefaults()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "not json");
        var store = new SettingsStore(path);

        AppSettings loaded = store.Load();

        Assert.Equal(TranscribeModelKind.Gpt4oTranscribe, loaded.TranscribeModelKind);
        Assert.Empty(loaded.RecognitionVocabulary);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
