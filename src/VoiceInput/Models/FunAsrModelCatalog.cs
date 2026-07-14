namespace VoiceInput.Models;

internal sealed record FunAsrArtifact(string RelativePath, Uri Url, long Size, string Sha256);

internal enum FunAsrRunnerKind
{
    SenseVoice,
    Paraformer,
    Nano,
}

internal sealed record FunAsrModelDefinition(
    string Id,
    string DisplayName,
    string Description,
    FunAsrRunnerKind Runner,
    IReadOnlySet<string> Languages,
    IReadOnlyList<FunAsrArtifact> Artifacts,
    Uri Source,
    Uri License)
{
    public long DownloadSize => Artifacts.Sum(artifact => artifact.Size);

    public bool Supports(string language) => Languages.Contains(language);
}

internal static class FunAsrModelCatalog
{
    public const string DefaultId = "sensevoice-small-q8";
    public const string RuntimeVersion = "v0.1.5";

    private static readonly Uri ApacheLicense = new("https://www.apache.org/licenses/LICENSE-2.0");

    public static FunAsrArtifact Runtime { get; } = new(
        "runtime-v0.1.5.zip",
        new("https://github.com/modelscope/FunASR/releases/download/runtime-llamacpp-v0.1.5/funasr-llamacpp-windows-x64.zip"),
        4_663_321,
        "2398192c1dd965a3d6c150833757a55047fa616a8b3561dd4d674259a913afbd");

    public static FunAsrArtifact Vad { get; } = new(
        "shared/fsmn-vad.gguf",
        new("https://huggingface.co/FunAudioLLM/fsmn-vad-GGUF/resolve/6840bae4c5c92ee8c04faaf4db23dd0105098d7f/fsmn-vad.gguf"),
        1_720_512,
        "1270f2559c495f4e7b6e739541151027d360761a3fda43fc147034f5719f5479");

    public static IReadOnlyList<FunAsrModelDefinition> Models { get; } =
    [
        new(
            DefaultId,
            "SenseVoiceSmall",
            "Balanced local recognition for Chinese, English, Japanese, and Korean.",
            FunAsrRunnerKind.SenseVoice,
            Languages("en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR"),
            [new(
                "models/sensevoice-small-q8/sensevoice-small-q8.gguf",
                new("https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/90c1c61912018b70ada0fcc024ea24aca62f2e63/sensevoice-small-q8.gguf"),
                254_208_320,
                "4ae45c94422de949b387e2e0fb10d7e14e4c42c69db30c3444ecc7d4b844b7c5")],
            new("https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF"),
            ApacheLicense),
        new(
            "paraformer-zh-q8",
            "Paraformer Chinese",
            "Fast Chinese and English recognition.",
            FunAsrRunnerKind.Paraformer,
            Languages("en-US", "zh-CN", "zh-TW"),
            [new(
                "models/paraformer-zh-q8/paraformer-q8.gguf",
                new("https://huggingface.co/FunAudioLLM/Paraformer-GGUF/resolve/de2cbaaa0f30b34f398d7a066fdfefb8e50d902c/paraformer-q8.gguf"),
                236_929_024,
                "42bf76ea1575a336aaca4c1b7c01a82b79113e6d04d0d6b799561bfcf07ee011")],
            new("https://huggingface.co/FunAudioLLM/Paraformer-GGUF"),
            ApacheLicense),
        new(
            "fun-asr-nano-q4",
            "Fun-ASR Nano",
            "Higher-quality recognition for difficult vocabulary and accents.",
            FunAsrRunnerKind.Nano,
            Languages("en-US", "zh-CN", "zh-TW", "ja-JP"),
            [
                new(
                    "models/fun-asr-nano-q4/funasr-encoder-f16.gguf",
                    new("https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF/resolve/c1629cbf83548ea0d92077c09d3541ce407ee643/funasr-encoder-f16.gguf"),
                    469_331_008,
                    "f92f91d01a24fbed6c863495b2ee8c6a6788144a02858b75743f0946668de8a2"),
                new(
                    "models/fun-asr-nano-q4/qwen3-0.6b-q4km.gguf",
                    new("https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF/resolve/c1629cbf83548ea0d92077c09d3541ce407ee643/qwen3-0.6b-q4km.gguf"),
                    484_219_776,
                    "cc5057552aa9dddedcda73ea8889854e8a257eb07d0a561b7234465c1e856f22"),
            ],
            new("https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF"),
            ApacheLicense),
    ];

    public static FunAsrModelDefinition Default => Models[0];

    public static FunAsrModelDefinition Get(string id) =>
        Models.FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentException($"Unknown FunASR model '{id}'.", nameof(id));

    public static string NormalizeId(string? id) =>
        Models.Any(model => model.Id == id) ? id! : DefaultId;

    private static IReadOnlySet<string> Languages(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
