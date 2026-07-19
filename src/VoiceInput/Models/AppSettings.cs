namespace VoiceInput.Models;

/// <summary>Which speech-to-text backend to use.</summary>
public enum SpeechEngineKind
{
    /// <summary>Windows on-device dictation (Windows.Media.SpeechRecognition). Default; works without an API key.</summary>
    Windows,
    /// <summary>Azure Speech SDK streaming recognition. Best zh-CN quality; requires a key + region.</summary>
    Azure,
    /// <summary>Batch transcription via an Azure AI Foundry gpt-4o-transcribe deployment (Entra auth).</summary>
    GptTranscribe,
    /// <summary>Batch transcription with an app-managed on-device model.</summary>
    FunAsr,
}

/// <summary>How VoiceInput authenticates to the Azure Speech resource.</summary>
public enum AzureAuthMode
{
    /// <summary>Account key (local auth). Simplest; fails if the resource has key auth disabled.</summary>
    Key,
    /// <summary>Microsoft Entra ID (AAD). Uses your signed-in identity; works when key auth is disabled.</summary>
    EntraId,
}

public enum PttMode
{
    Hold,
    Toggle,
}

public enum OverlayPosition
{
    Top,
    Bottom,
}

/// <summary>A fixed input profile with user-editable presentation and activation settings.</summary>
public sealed class InputProfile
{
    public const string Profile1Id = "profile-1";
    public const string Profile2Id = "profile-2";
    public const int MaxNameLength = 24;

    private static readonly HashSet<string> SupportedKeys = new(StringComparer.Ordinal)
    {
        "RightCtrl",
        "LeftCtrl",
        "CapsLock",
        "RightAlt",
        "RightShift",
    };

    public InputProfile()
    {
    }

    public InputProfile(
        string id,
        string name,
        string pttKey,
        PttMode pttMode,
        OverlayPosition overlayPosition)
    {
        Id = id;
        Name = name;
        PttKey = pttKey;
        PttMode = pttMode;
        OverlayPosition = overlayPosition;
    }

    public string Id { get; set; } = Profile1Id;
    public string Name { get; set; } = "Desktop";
    public string PttKey { get; set; } = "RightCtrl";
    public PttMode PttMode { get; set; } = PttMode.Hold;
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.Bottom;

    public InputProfile Clone() => new(Id, Name, PttKey, PttMode, OverlayPosition);

    public static string NormalizeId(string? id) =>
        string.Equals(id, Profile2Id, StringComparison.Ordinal) ? Profile2Id : Profile1Id;

    public static bool IsSupportedKey(string? key) =>
        key is not null && SupportedKeys.Contains(key);

    internal static InputProfile[] CreateDefaults(
        string primaryKey = "RightCtrl",
        PttMode primaryMode = PttMode.Hold) =>
    [
        new(Profile1Id, "Desktop", NormalizeKey(primaryKey, "RightCtrl"),
            NormalizeMode(primaryMode, PttMode.Hold), OverlayPosition.Bottom),
        new(Profile2Id, "Mobile", "LeftCtrl", PttMode.Toggle, OverlayPosition.Top),
    ];

    internal static InputProfile[] Normalize(
        IEnumerable<InputProfile?>? profiles,
        string primaryKey = "RightCtrl",
        PttMode primaryMode = PttMode.Hold)
    {
        InputProfile[] defaults = CreateDefaults(primaryKey, primaryMode);
        InputProfile?[] supplied = profiles?.ToArray() ?? [];
        InputProfile first = NormalizeOne(
            supplied.FirstOrDefault(profile => profile?.Id == Profile1Id),
            defaults[0]);
        InputProfile second = NormalizeOne(
            supplied.FirstOrDefault(profile => profile?.Id == Profile2Id),
            defaults[1]);

        if (string.Equals(first.Name, second.Name, StringComparison.OrdinalIgnoreCase))
        {
            second.Name = !string.Equals(first.Name, "Mobile", StringComparison.OrdinalIgnoreCase)
                ? "Mobile"
                : "Profile 2";
        }

        return [first, second];
    }

    private static InputProfile NormalizeOne(InputProfile? value, InputProfile fallback) => new(
        fallback.Id,
        NormalizeName(value?.Name, fallback.Name),
        NormalizeKey(value?.PttKey, fallback.PttKey),
        NormalizeMode(value?.PttMode ?? fallback.PttMode, fallback.PttMode),
        NormalizePosition(value?.OverlayPosition ?? fallback.OverlayPosition, fallback.OverlayPosition));

    private static string NormalizeName(string? value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    private static string NormalizeKey(string? value, string fallback) =>
        IsSupportedKey(value) ? value! : fallback;

    private static PttMode NormalizeMode(PttMode value, PttMode fallback) =>
        Enum.IsDefined(value) ? value : fallback;

    private static OverlayPosition NormalizePosition(OverlayPosition value, OverlayPosition fallback) =>
        Enum.IsDefined(value) ? value : fallback;
}

/// <summary>
/// In-memory application settings. Secrets (Azure key, LLM key) are held in plaintext here
/// but are DPAPI-encrypted at rest by <see cref="Services.SettingsStore"/>.
/// </summary>
public sealed class AppSettings
{
    private InputProfile[] _profiles = InputProfile.CreateDefaults();
    private string _activeProfileId = InputProfile.Profile1Id;

    /// <summary>False until a new user completes or explicitly skips the first-run guide.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Recognition language. Default Simplified Chinese so it works out of the box for zh-CN.</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>Activation key for the active profile.</summary>
    public string PttKey
    {
        get => ActiveProfile.PttKey;
        set => ActiveProfile.PttKey = InputProfile.IsSupportedKey(value) ? value : ActiveProfile.PttKey;
    }

    /// <summary>Activation behavior for the active profile.</summary>
    public PttMode PttMode
    {
        get => ActiveProfile.PttMode;
        set
        {
            if (Enum.IsDefined(value))
                ActiveProfile.PttMode = value;
        }
    }

    public InputProfile[] Profiles
    {
        get => _profiles;
        set => _profiles = InputProfile.Normalize(value);
    }

    public string ActiveProfileId
    {
        get => _activeProfileId;
        set => _activeProfileId = InputProfile.NormalizeId(value);
    }

    public InputProfile ActiveProfile => GetProfile(ActiveProfileId);

    public InputProfile GetProfile(string id)
    {
        string normalized = InputProfile.NormalizeId(id);
        return _profiles.First(profile => profile.Id == normalized);
    }

    public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Windows;

    /// <summary>Stable ID of the app-managed local model.</summary>
    public string FunAsrModelId { get; set; } = FunAsrModelCatalog.DefaultId;

    // --- Azure Speech ---
    public string AzureKey { get; set; } = string.Empty;
    public string AzureRegion { get; set; } = "eastasia";

    /// <summary>Key or Microsoft Entra ID auth for the Azure Speech resource. Default Key (back-compat).</summary>
    public AzureAuthMode AzureAuthMode { get; set; } = AzureAuthMode.Key;

    /// <summary>Custom-domain endpoint of the resource, e.g. https://my-resource.cognitiveservices.azure.com/.
    /// Required for Entra ID auth.</summary>
    public string AzureEndpoint { get; set; } = string.Empty;

    /// <summary>Entra tenant that owns the Speech resource. Blank = the credential's default tenant.</summary>
    public string AzureTenantId { get; set; } = string.Empty;

    // --- gpt-4o-transcribe (Azure AI Foundry, batch, Entra-only) ---
    /// <summary>Foundry resource custom-domain endpoint, e.g. https://my-resource.cognitiveservices.azure.com/.</summary>
    public string TranscribeEndpoint { get; set; } = string.Empty;

    /// <summary>Deployment name of the transcription model.</summary>
    public string TranscribeModel { get; set; } = "gpt-4o-transcribe";

    /// <summary>Capability metadata for the model behind the configured deployment.</summary>
    public TranscribeModelKind TranscribeModelKind { get; set; } = TranscribeModelKind.Gpt4oTranscribe;

    /// <summary>Names and domain terms supplied to recognition engines that support vocabulary hints.</summary>
    public string[] RecognitionVocabulary { get; set; } = [];

    /// <summary>Key or Microsoft Entra ID auth for the Foundry transcription resource. Default Entra.</summary>
    public AzureAuthMode TranscribeAuthMode { get; set; } = AzureAuthMode.EntraId;

    /// <summary>Account key for the transcription resource (used when <see cref="TranscribeAuthMode"/> is Key).</summary>
    public string TranscribeApiKey { get; set; } = string.Empty;

    /// <summary>Entra tenant that owns the Foundry resource. Blank = the credential's default tenant.</summary>
    public string TranscribeTenantId { get; set; } = string.Empty;

    // --- LLM refinement (OpenAI-compatible) ---
    public bool LlmEnabled { get; set; }
    public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string LlmApiKey { get; set; } = string.Empty;
    public string LlmModel { get; set; } = "gpt-4.1-mini";

    /// <summary>Custom refine system prompt. Blank = use the built-in speech-aware default.</summary>
    public string LlmPrompt { get; set; } = string.Empty;

    /// <summary>Correction rules learned from the user's edits, appended to the refine prompt.</summary>
    public string LlmLearnedRules { get; set; } = string.Empty;

    /// <summary>When true, capture (recognized → your edited) pairs on Enter for later learning.</summary>
    public bool LearnFromEdits { get; set; }

    /// <summary>When true, the log records transcript / LLM text verbatim (for debugging).
    /// Off by default — dictated speech can contain passwords and other secrets.</summary>
    public bool DiagnosticLogging { get; set; }

    /// <summary>When true, read the focused control / terminal text via UI Automation and pass it
    /// to the LLM as context for better correction. Off by default — it sends surrounding app
    /// content (which may be sensitive) to the LLM.</summary>
    public bool UseContext { get; set; }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.RecognitionVocabulary = (string[])RecognitionVocabulary.Clone();
        clone._profiles = Profiles.Select(profile => profile.Clone()).ToArray();
        return clone;
    }

    /// <summary>The five recognition languages offered in the tray menu.</summary>
    public static readonly (string Code, string Display)[] SupportedLanguages =
    {
        ("en-US", "English"),
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("ja-JP", "日本語"),
        ("ko-KR", "한국어"),
        ("vi-VN", "Tiếng Việt"),
    };
}
