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
    private readonly string _dir;
    private readonly string _filePath;

    public SettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceInput", "settings.json"))
    {
    }

    internal SettingsStore(string filePath)
    {
        _filePath = filePath;
        _dir = Path.GetDirectoryName(filePath) ?? throw new ArgumentException("Settings path must include a directory.", nameof(filePath));
    }

    /// <summary>False on a fresh machine (no settings saved yet) — used to drive first-run onboarding.</summary>
    public bool Exists => File.Exists(_filePath);

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
            if (!File.Exists(_filePath))
            {
                return new AppSettings
                {
                    Engine = SpeechEngineKind.FunAsr,
                    FunAsrModelId = FunAsrModelCatalog.DefaultId,
                };
            }

            var dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (dto is null)
                return new AppSettings();

            return new AppSettings
            {
                OnboardingCompleted = dto.OnboardingCompleted,
                Language = dto.Language,
                PttKey = dto.PttKey,
                PttMode = dto.PttMode,
                Engine = dto.Engine,
                FunAsrModelId = FunAsrModelCatalog.NormalizeId(dto.FunAsrModelId),
                AzureRegion = dto.AzureRegion,
                AzureKey = Unprotect(dto.AzureKeyEnc),
                AzureAuthMode = dto.AzureAuthMode,
                AzureEndpoint = dto.AzureEndpoint,
                AzureTenantId = dto.AzureTenantId,
                TranscribeEndpoint = dto.TranscribeEndpoint,
                TranscribeModel = dto.TranscribeModel,
                TranscribeAuthMode = dto.TranscribeAuthMode,
                TranscribeApiKey = Unprotect(dto.TranscribeApiKeyEnc),
                TranscribeTenantId = dto.TranscribeTenantId,
                LlmEnabled = dto.LlmEnabled,
                LlmBaseUrl = dto.LlmBaseUrl,
                LlmModel = dto.LlmModel,
                LlmPrompt = dto.LlmPrompt,
                LlmLearnedRules = dto.LlmLearnedRules,
                LearnFromEdits = dto.LearnFromEdits,
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
        Directory.CreateDirectory(_dir);
        var dto = new PersistedSettings
        {
            OnboardingCompleted = s.OnboardingCompleted,
            Language = s.Language,
            PttKey = s.PttKey,
            PttMode = s.PttMode,
            Engine = s.Engine,
            FunAsrModelId = FunAsrModelCatalog.NormalizeId(s.FunAsrModelId),
            AzureRegion = s.AzureRegion,
            AzureKeyEnc = Protect(s.AzureKey),
            AzureAuthMode = s.AzureAuthMode,
            AzureEndpoint = s.AzureEndpoint,
            AzureTenantId = s.AzureTenantId,
            TranscribeEndpoint = s.TranscribeEndpoint,
            TranscribeModel = s.TranscribeModel,
            TranscribeAuthMode = s.TranscribeAuthMode,
            TranscribeApiKeyEnc = Protect(s.TranscribeApiKey),
            TranscribeTenantId = s.TranscribeTenantId,
            LlmEnabled = s.LlmEnabled,
            LlmBaseUrl = s.LlmBaseUrl,
            LlmModel = s.LlmModel,
            LlmPrompt = s.LlmPrompt,
            LlmLearnedRules = s.LlmLearnedRules,
            LearnFromEdits = s.LearnFromEdits,
            LlmApiKeyEnc = Protect(s.LlmApiKey),
            DiagnosticLogging = s.DiagnosticLogging,
            UseContext = s.UseContext,
        };
        string temp = _filePath + ".tmp";
        string backup = _filePath + ".backup";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, JsonOptions));
            if (File.Exists(_filePath))
            {
                File.Replace(temp, _filePath, backup);
                try { File.Delete(backup); }
                catch (Exception ex) { Log.Write($"Settings backup cleanup failed: {ex.Message}"); }
            }
            else
            {
                File.Move(temp, _filePath);
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { /* Preserve the original save exception. */ }
        }
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
        // Missing on settings written before the onboarding feature; existing users stay completed.
        public bool OnboardingCompleted { get; set; } = true;
        public string Language { get; set; } = "zh-CN";
        public string PttKey { get; set; } = "RightCtrl";
        public PttMode PttMode { get; set; } = PttMode.Hold;
        public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Windows;
        public string FunAsrModelId { get; set; } = FunAsrModelCatalog.DefaultId;
        public string AzureRegion { get; set; } = "eastasia";
        public string AzureKeyEnc { get; set; } = string.Empty;
        public AzureAuthMode AzureAuthMode { get; set; } = AzureAuthMode.Key;
        public string AzureEndpoint { get; set; } = string.Empty;
        public string AzureTenantId { get; set; } = string.Empty;
        public string TranscribeEndpoint { get; set; } = string.Empty;
        public string TranscribeModel { get; set; } = "gpt-4o-transcribe";
        public AzureAuthMode TranscribeAuthMode { get; set; } = AzureAuthMode.EntraId;
        public string TranscribeApiKeyEnc { get; set; } = string.Empty;
        public string TranscribeTenantId { get; set; } = string.Empty;
        public bool LlmEnabled { get; set; }
        public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
        public string LlmModel { get; set; } = "gpt-4.1-mini";
        public string LlmPrompt { get; set; } = string.Empty;
        public string LlmLearnedRules { get; set; } = string.Empty;
        public bool LearnFromEdits { get; set; }
        public string LlmApiKeyEnc { get; set; } = string.Empty;
        public bool DiagnosticLogging { get; set; }
        public bool UseContext { get; set; }
    }
}
