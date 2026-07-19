using System.Runtime.Intrinsics.X86;

namespace VoiceInput.Models;

internal sealed record FunAsrArtifact(string RelativePath, Uri Url, long Size, string Sha256);

internal enum FunAsrRunnerKind
{
    SenseVoice,
    Paraformer,
    Nano,
    Qwen3Asr,
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

    public bool UsesFunAsrRuntime => Runner != FunAsrRunnerKind.Qwen3Asr;

    public bool Supports(string language) => Languages.Contains(language);
}

internal static class FunAsrModelCatalog
{
    public const string SenseVoiceId = "sensevoice-small-q8";
    public const string Qwen3AsrId = "qwen3-asr-0.6b-int8";
    public const string Qwen3Asr17BId = "qwen3-asr-1.7b-int8";
    public const string DefaultId = Qwen3AsrId;
    public const string RuntimeVersion = "v0.1.5";

    private const string Qwen06BaseUrl =
        "https://huggingface.co/csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25/resolve/68818b2313fe77bd06f6a7c5068ff3ef59d02b8a";
    private const string Qwen17BaseUrl =
        "https://www.modelscope.cn/models/zengshuishui/Qwen3-ASR-onnx/resolve/cb045ad80b8970c9d411d463e5b78991a566596c";
    private static readonly Uri ApacheLicense = new("https://www.apache.org/licenses/LICENSE-2.0");

    private static readonly FunAsrArtifact GenericRuntime = new(
        "runtime-v0.1.5.zip",
        new("https://github.com/modelscope/FunASR/releases/download/runtime-llamacpp-v0.1.5/funasr-llamacpp-windows-x64.zip"),
        4_663_321,
        "2398192c1dd965a3d6c150833757a55047fa616a8b3561dd4d674259a913afbd");

    private static readonly FunAsrArtifact Avx2Runtime = new(
        "runtime-v0.1.5-avx2.zip",
        new("https://github.com/modelscope/FunASR/releases/download/runtime-llamacpp-v0.1.5/funasr-llamacpp-windows-x64-avx2.zip"),
        4_895_618,
        "f51482d8acdf8c50a9e8822f1acf074d7fe849f859eb251cf427bca28fb0dbd0");

    public static FunAsrArtifact Runtime { get; } = SelectRuntime(Avx2.IsSupported);

    internal static FunAsrArtifact SelectRuntime(bool avx2Supported) =>
        avx2Supported ? Avx2Runtime : GenericRuntime;

    public static FunAsrArtifact Vad { get; } = new(
        "shared/fsmn-vad.gguf",
        new("https://huggingface.co/FunAudioLLM/fsmn-vad-GGUF/resolve/6840bae4c5c92ee8c04faaf4db23dd0105098d7f/fsmn-vad.gguf"),
        1_720_512,
        "1270f2559c495f4e7b6e739541151027d360761a3fda43fc147034f5719f5479");

    public static IReadOnlyList<FunAsrModelDefinition> Models { get; } =
    [
        new(
            SenseVoiceId,
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
        new(
            Qwen3AsrId,
            "Qwen3-ASR 0.6B",
            "High-quality multilingual recognition with vocabulary hints. CPU-only; use clips up to 25 seconds.",
            FunAsrRunnerKind.Qwen3Asr,
            Languages("en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "vi-VN"),
            [
                QwenArtifact(
                    Qwen3AsrId,
                    Qwen06BaseUrl,
                    string.Empty,
                    "conv_frontend.onnx",
                    44_148_281,
                    "d22dc4423e0940e49884e903d2ea2f7e5567c14fc1aed97e4e26d6b8f208ef9e"),
                QwenArtifact(
                    Qwen3AsrId,
                    Qwen06BaseUrl,
                    string.Empty,
                    "encoder.int8.onnx",
                    182_491_662,
                    "60748d3e6744a57c9c91e1b17424a6c2990567e8adceb0783940c03ed98fa9d9"),
                QwenArtifact(
                    Qwen3AsrId,
                    Qwen06BaseUrl,
                    string.Empty,
                    "decoder.int8.onnx",
                    755_914_231,
                    "4f6885be5959ae26af3089d38ee7972c5fafbeeb1cf8d5e76eab6d8b61ca5771"),
                QwenArtifact(
                    Qwen3AsrId,
                    Qwen06BaseUrl,
                    string.Empty,
                    "tokenizer/merges.txt",
                    1_671_853,
                    "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5"),
                QwenArtifact(
                    Qwen3AsrId,
                    Qwen06BaseUrl,
                    string.Empty,
                    "tokenizer/tokenizer_config.json",
                    12_487,
                    "4942d005604266809309cabc9f4e9cb89ce855d59b14681fdc0e1cc62ea26c4c"),
                QwenArtifact(
                    Qwen3AsrId,
                    Qwen06BaseUrl,
                    string.Empty,
                    "tokenizer/vocab.json",
                    2_776_833,
                    "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910"),
            ],
            new("https://huggingface.co/csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25"),
            ApacheLicense),
        new(
            Qwen3Asr17BId,
            "Qwen3-ASR 1.7B",
            "Higher-accuracy multilingual recognition. CPU-only and substantially larger than 0.6B.",
            FunAsrRunnerKind.Qwen3Asr,
            Languages("en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "vi-VN"),
            [
                QwenArtifact(
                    Qwen3Asr17BId,
                    Qwen17BaseUrl,
                    "model_1.7B",
                    "conv_frontend.onnx",
                    48_080_441,
                    "fa894a4ba53da6a4238f2a6ca0b09362e505d39cecbd646051b033e2e8d7e2fb"),
                QwenArtifact(
                    Qwen3Asr17BId,
                    Qwen17BaseUrl,
                    "model_1.7B",
                    "encoder.int8.onnx",
                    314_222_162,
                    "436fbd910a0c8914851e5ac1354e807be9f283d08a5da728adaa609731c41469"),
                QwenArtifact(
                    Qwen3Asr17BId,
                    Qwen17BaseUrl,
                    "model_1.7B",
                    "decoder.int8.onnx",
                    2_037_458_645,
                    "c43c853fa6e97d08365cb8a5502b360b595cd43c00dc60e4d8ca7cc18cad460b"),
                QwenArtifact(
                    Qwen3Asr17BId,
                    Qwen17BaseUrl,
                    "tokenizer",
                    "merges.txt",
                    1_671_853,
                    "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5",
                    localPrefix: "tokenizer"),
                QwenArtifact(
                    Qwen3Asr17BId,
                    Qwen17BaseUrl,
                    "tokenizer",
                    "tokenizer_config.json",
                    12_487,
                    "4942d005604266809309cabc9f4e9cb89ce855d59b14681fdc0e1cc62ea26c4c",
                    localPrefix: "tokenizer"),
                QwenArtifact(
                    Qwen3Asr17BId,
                    Qwen17BaseUrl,
                    "tokenizer",
                    "vocab.json",
                    2_776_833,
                    "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
                    localPrefix: "tokenizer"),
            ],
            new("https://www.modelscope.cn/models/zengshuishui/Qwen3-ASR-onnx"),
            ApacheLicense),
    ];

    public static FunAsrModelDefinition Default => Get(DefaultId);

    public static FunAsrModelDefinition Get(string id) =>
        Models.FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentException($"Unknown FunASR model '{id}'.", nameof(id));

    public static string NormalizeId(string? id) =>
        Models.Any(model => model.Id == id) ? id! : DefaultId;

    private static IReadOnlySet<string> Languages(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    private static FunAsrArtifact QwenArtifact(
        string modelId,
        string baseUrl,
        string remotePrefix,
        string path,
        long size,
        string sha256,
        string? localPrefix = null)
    {
        string remotePath = string.IsNullOrEmpty(remotePrefix) ? path : $"{remotePrefix}/{path}";
        string localPath = string.IsNullOrEmpty(localPrefix) ? path : $"{localPrefix}/{path}";
        return new(
            $"models/{modelId}/{localPath}",
            new($"{baseUrl}/{remotePath}"),
            size,
            sha256);
    }
}
