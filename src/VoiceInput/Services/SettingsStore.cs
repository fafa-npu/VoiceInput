using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceInput.Models;

namespace VoiceInput.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> to %APPDATA%\VoiceInput\settings.json.
/// Secret fields are encrypted at rest with Windows DPAPI (per-user scope).
/// </summary>
public sealed class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceInput");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    // App-specific entropy mixed into DPAPI so another process running as the same
    // user can't trivially unprotect the blob without also knowing this value.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoiceInput.v1.dpapi.entropy");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (dto is null)
                return new AppSettings();

            return new AppSettings
            {
                Language = dto.Language,
                PttKey = dto.PttKey,
                Engine = dto.Engine,
                AzureRegion = dto.AzureRegion,
                AzureKey = Unprotect(dto.AzureKeyEnc),
                LlmEnabled = dto.LlmEnabled,
                LlmBaseUrl = dto.LlmBaseUrl,
                LlmModel = dto.LlmModel,
                LlmPrompt = dto.LlmPrompt,
                LlmApiKey = Unprotect(dto.LlmApiKeyEnc),
                DiagnosticLogging = dto.DiagnosticLogging,
                UseContext = dto.UseContext,
            };
        }
        catch
        {
            // Corrupt or unreadable settings should never crash startup.
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        var dto = new PersistedSettings
        {
            Language = s.Language,
            PttKey = s.PttKey,
            Engine = s.Engine,
            AzureRegion = s.AzureRegion,
            AzureKeyEnc = Protect(s.AzureKey),
            LlmEnabled = s.LlmEnabled,
            LlmBaseUrl = s.LlmBaseUrl,
            LlmModel = s.LlmModel,
            LlmPrompt = s.LlmPrompt,
            LlmApiKeyEnc = Protect(s.LlmApiKey),
            DiagnosticLogging = s.DiagnosticLogging,
            UseContext = s.UseContext,
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    private static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || !OperatingSystem.IsWindows())
            return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher) || !OperatingSystem.IsWindows())
            return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>On-disk shape. Secret fields hold DPAPI base64, never plaintext.</summary>
    private sealed class PersistedSettings
    {
        public string Language { get; set; } = "zh-CN";
        public string PttKey { get; set; } = "RightCtrl";
        public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Windows;
        public string AzureRegion { get; set; } = "eastasia";
        public string AzureKeyEnc { get; set; } = string.Empty;
        public bool LlmEnabled { get; set; }
        public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
        public string LlmModel { get; set; } = "gpt-4.1-mini";
        public string LlmPrompt { get; set; } = string.Empty;
        public string LlmApiKeyEnc { get; set; } = string.Empty;
        public bool DiagnosticLogging { get; set; }
        public bool UseContext { get; set; }
    }
}
