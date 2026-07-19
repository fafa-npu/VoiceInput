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
                var fresh = new AppSettings
                {
                    Engine = SpeechEngineKind.FunAsr,
                    FunAsrModelId = FunAsrModelCatalog.DefaultId,
                };
                Log.Write($"Vocabulary settings-load modelKind={fresh.TranscribeModelKind} configured=0 accepted=0 rejected=0");
                return fresh;
            }

            var dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (dto is null)
                throw new JsonException("Settings JSON root was null.");

            TranscribeModelKind modelKind = ParseModelKind(dto.TranscribeModelKind);
            (RecognitionVocabularyNormalization vocabulary, bool invalidVocabularyShape) =
                ParseVocabulary(dto.RecognitionVocabulary);
            (InputProfile[] profiles, bool invalidProfilesShape) = ParseProfiles(
                dto.Profiles,
                dto.PttKey,
                dto.PttMode);

            var settings = new AppSettings
            {
                OnboardingCompleted = dto.OnboardingCompleted,
                Language = dto.Language,
                Profiles = profiles,
                ActiveProfileId = dto.ActiveProfileId,
                Engine = dto.Engine,
                FunAsrModelId = FunAsrModelCatalog.NormalizeId(dto.FunAsrModelId),
                AzureRegion = dto.AzureRegion,
                AzureKey = Unprotect(dto.AzureKeyEnc),
                AzureAuthMode = dto.AzureAuthMode,
                AzureEndpoint = dto.AzureEndpoint,
                AzureTenantId = dto.AzureTenantId,
                TranscribeEndpoint = dto.TranscribeEndpoint,
                TranscribeModel = dto.TranscribeModel,
                TranscribeModelKind = modelKind,
                RecognitionVocabulary = vocabulary.Entries,
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

            string level = modelKind == TranscribeModelKind.Unknown || invalidVocabularyShape ? "WARN " : string.Empty;
            Log.Write($"{level}Vocabulary settings-load modelKind={modelKind} configured={vocabulary.ConfiguredCount} " +
                $"accepted={vocabulary.AcceptedCount} rejected={vocabulary.RejectedCount}");
            Log.Write($"{(invalidProfilesShape ? "WARN " : string.Empty)}Profile settings-load " +
                $"active={settings.ActiveProfileId} name={settings.ActiveProfile.Name} " +
                $"key={settings.PttKey} mode={settings.PttMode} overlay={settings.ActiveProfile.OverlayPosition}");
            return settings;
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable settings should never crash startup.
            Log.Error("Settings load", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(_dir);
        RecognitionVocabularyNormalization vocabulary =
            RecognitionVocabulary.Normalize(s.RecognitionVocabulary ?? []);
        TranscribeModelKind modelKind = Enum.IsDefined(s.TranscribeModelKind)
            ? s.TranscribeModelKind
            : TranscribeModelKind.Unknown;
        InputProfile[] profiles = InputProfile.Normalize(s.Profiles, s.PttKey, s.PttMode);
        string activeProfileId = InputProfile.NormalizeId(s.ActiveProfileId);
        InputProfile activeProfile = profiles.First(profile => profile.Id == activeProfileId);
        var dto = new PersistedSettings
        {
            OnboardingCompleted = s.OnboardingCompleted,
            Language = s.Language,
            PttKey = activeProfile.PttKey,
            PttMode = activeProfile.PttMode,
            Profiles = JsonSerializer.SerializeToElement(profiles, JsonOptions),
            ActiveProfileId = activeProfileId,
            Engine = s.Engine,
            FunAsrModelId = FunAsrModelCatalog.NormalizeId(s.FunAsrModelId),
            AzureRegion = s.AzureRegion,
            AzureKeyEnc = Protect(s.AzureKey),
            AzureAuthMode = s.AzureAuthMode,
            AzureEndpoint = s.AzureEndpoint,
            AzureTenantId = s.AzureTenantId,
            TranscribeEndpoint = s.TranscribeEndpoint,
            TranscribeModel = s.TranscribeModel,
            TranscribeModelKind = JsonSerializer.SerializeToElement(modelKind.ToString()),
            RecognitionVocabulary = JsonSerializer.SerializeToElement(vocabulary.Entries),
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

        RecognitionVocabularyMode mode = RecognitionVocabulary.ResolveMode(s);
        string level = modelKind == TranscribeModelKind.Unknown ? "WARN " : string.Empty;
        Log.Write($"{level}Vocabulary settings-save engine={s.Engine} modelKind={modelKind} mode={mode} " +
            $"configured={vocabulary.ConfiguredCount} accepted={vocabulary.AcceptedCount} rejected={vocabulary.RejectedCount}");
        Log.Write($"Profile settings-save active={activeProfileId} name={activeProfile.Name} " +
            $"key={activeProfile.PttKey} mode={activeProfile.PttMode} overlay={activeProfile.OverlayPosition}");
    }

    private static TranscribeModelKind ParseModelKind(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return TranscribeModelKind.Unknown;

        string? text = value.GetString();
        return Enum.TryParse(text, ignoreCase: true, out TranscribeModelKind modelKind) &&
               Enum.IsDefined(modelKind) &&
               string.Equals(text, modelKind.ToString(), StringComparison.OrdinalIgnoreCase)
            ? modelKind
            : TranscribeModelKind.Unknown;
    }

    private static (RecognitionVocabularyNormalization Vocabulary, bool InvalidShape) ParseVocabulary(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (RecognitionVocabulary.Normalize([]), false);

        if (value.ValueKind != JsonValueKind.Array)
            return (RecognitionVocabulary.Normalize([]), true);

        var strings = new List<string?>();
        int nonStringCount = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                strings.Add(item.GetString());
            else
                nonStringCount++;
        }

        RecognitionVocabularyNormalization normalized = RecognitionVocabulary.Normalize(strings);
        return (new RecognitionVocabularyNormalization(
            normalized.Entries,
            normalized.ConfiguredCount + nonStringCount,
            normalized.RejectedCount + nonStringCount),
            nonStringCount > 0);
    }

    private static (InputProfile[] Profiles, bool InvalidShape) ParseProfiles(
        JsonElement value,
        string legacyKey,
        PttMode legacyMode)
    {
        InputProfile[] fallback = InputProfile.CreateDefaults(legacyKey, legacyMode);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (fallback, false);
        if (value.ValueKind != JsonValueKind.Array)
            return (fallback, true);

        try
        {
            InputProfile?[]? parsed = JsonSerializer.Deserialize<InputProfile?[]>(value.GetRawText(), JsonOptions);
            bool hasBothProfiles = parsed is not null
                && parsed.Any(profile => profile?.Id == InputProfile.Profile1Id)
                && parsed.Any(profile => profile?.Id == InputProfile.Profile2Id);
            return (InputProfile.Normalize(parsed, legacyKey, legacyMode), !hasBothProfiles);
        }
        catch (JsonException)
        {
            return (fallback, true);
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
        public JsonElement Profiles { get; set; }
        public string ActiveProfileId { get; set; } = InputProfile.Profile1Id;
        public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Windows;
        public string FunAsrModelId { get; set; } = FunAsrModelCatalog.DefaultId;
        public string AzureRegion { get; set; } = "eastasia";
        public string AzureKeyEnc { get; set; } = string.Empty;
        public AzureAuthMode AzureAuthMode { get; set; } = AzureAuthMode.Key;
        public string AzureEndpoint { get; set; } = string.Empty;
        public string AzureTenantId { get; set; } = string.Empty;
        public string TranscribeEndpoint { get; set; } = string.Empty;
        public string TranscribeModel { get; set; } = "gpt-4o-transcribe";
        public JsonElement TranscribeModelKind { get; set; }
        public JsonElement RecognitionVocabulary { get; set; }
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
