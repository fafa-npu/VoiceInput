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
}

/// <summary>How VoiceInput authenticates to the Azure Speech resource.</summary>
public enum AzureAuthMode
{
    /// <summary>Account key (local auth). Simplest; fails if the resource has key auth disabled.</summary>
    Key,
    /// <summary>Microsoft Entra ID (AAD). Uses your signed-in identity; works when key auth is disabled.</summary>
    EntraId,
}

/// <summary>
/// In-memory application settings. Secrets (Azure key, LLM key) are held in plaintext here
/// but are DPAPI-encrypted at rest by <see cref="Services.SettingsStore"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Recognition language. Default Simplified Chinese so it works out of the box for zh-CN.</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>Push-to-talk key. One of: RightCtrl, CapsLock, RightAlt, RightShift.</summary>
    public string PttKey { get; set; } = "RightCtrl";

    public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Windows;

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

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

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
