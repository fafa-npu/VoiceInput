namespace VoiceInput.Models;

/// <summary>Which speech-to-text backend to use.</summary>
public enum SpeechEngineKind
{
    /// <summary>Windows on-device dictation (Windows.Media.SpeechRecognition). Default; works without an API key.</summary>
    Windows,
    /// <summary>Azure Speech SDK streaming recognition. Best zh-CN quality; requires a key + region.</summary>
    Azure,
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

    // --- LLM refinement (OpenAI-compatible) ---
    public bool LlmEnabled { get; set; }
    public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string LlmApiKey { get; set; } = string.Empty;
    public string LlmModel { get; set; } = "gpt-4.1-mini";

    /// <summary>When true, the log records transcript / LLM text verbatim (for debugging).
    /// Off by default — dictated speech can contain passwords and other secrets.</summary>
    public bool DiagnosticLogging { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>The five recognition languages offered in the tray menu.</summary>
    public static readonly (string Code, string Display)[] SupportedLanguages =
    {
        ("en-US", "English"),
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("ja-JP", "日本語"),
        ("ko-KR", "한국어"),
    };
}
