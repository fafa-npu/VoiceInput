using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SherpaOnnx;

namespace VoiceInput.Services;

internal delegate Task<string> Qwen3AsrTranscriber(
    FunAsrResolvedModel model,
    byte[] pcm16kMono,
    string language,
    IReadOnlyList<string> vocabulary,
    CancellationToken cancellationToken);

/// <summary>
/// A batch Qwen3-ASR session. The heavyweight recognizer is owned by
/// <see cref="Qwen3AsrRecognizerHost"/> and survives these short-lived sessions.
/// </summary>
internal sealed class Qwen3AsrEngine : ISpeechEngine
{
    internal const int MaxAudioSeconds = 25;
    private const int BytesPerSecond = AudioCapture.TargetSampleRate * sizeof(short);

    private readonly FunAsrResolvedModel _model;
    private readonly Qwen3AsrTranscriber _transcribe;
    private readonly string[] _vocabulary;
    private readonly object _gate = new();
    private readonly MemoryStream _buffer = new();
    private CancellationTokenSource? _cancellation;
    private string _language = "zh-CN";
    private bool _started;
    private bool _closed;
    private bool _disposed;

    public Qwen3AsrEngine(
        FunAsrResolvedModel model,
        Qwen3AsrTranscriber transcribe,
        IEnumerable<string> vocabulary)
    {
        _model = model;
        _transcribe = transcribe;
        _vocabulary = vocabulary.ToArray();
    }

    public bool NeedsAudioFeed => true;
    public bool HasInterimResults => false;
    public int StopTimeoutMs => 120_000;

    public event Action<string>? Partial
    {
        add { }
        remove { }
    }
    public event Action<string>? Final;
    public event Action<SpeechFault>? Fault;

    public Task StartAsync(string language)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _buffer.SetLength(0);
            _language = language;
            _started = true;
            _closed = false;
        }
        return Task.CompletedTask;
    }

    public void Feed(byte[] pcm16kMono)
    {
        lock (_gate)
        {
            if (!_started || _closed || _disposed || _cancellation?.IsCancellationRequested == true)
                return;
            _buffer.Write(pcm16kMono, 0, pcm16kMono.Length);
        }
    }

    public async Task StopAsync()
    {
        byte[] pcm;
        string language;
        CancellationToken cancellationToken;
        lock (_gate)
        {
            if (!_started || _closed || _disposed)
                return;
            _closed = true;
            pcm = _buffer.ToArray();
            language = _language;
            cancellationToken = _cancellation?.Token ?? CancellationToken.None;
        }

        if (pcm.Length < 2 || cancellationToken.IsCancellationRequested)
            return;
        if (pcm.Length > MaxAudioSeconds * BytesPerSecond)
        {
            Fault?.Invoke(new(
                SpeechFaultKind.Service,
                $"{_model.Definition.DisplayName} supports up to {MaxAudioSeconds} seconds per dictation in this build. "
                + "Please record a shorter segment.",
                $"Captured {pcm.Length / (double)BytesPerSecond:F1} seconds; the ONNX decoder context is fixed."));
            return;
        }

        try
        {
            string transcript = (await _transcribe(
                _model,
                pcm,
                language,
                _vocabulary,
                cancellationToken).ConfigureAwait(false)).Trim();
            if (!cancellationToken.IsCancellationRequested && transcript.Length > 0)
                Final?.Invoke(transcript);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error("Qwen3AsrEngine.StopAsync", exception);
            Fault?.Invoke(new(
                SpeechFaultKind.Service,
                "Local Qwen3-ASR transcription failed. Check the selected model in Settings.",
                exception.Message));
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _started = false;
            _closed = true;
            _buffer.SetLength(0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _buffer.Dispose();
        }
    }

}

/// <summary>Owns one process-wide Qwen3-ASR recognizer and serializes native inference.</summary>
internal sealed class Qwen3AsrRecognizerHost : IDisposable
{
    // Qwen injects these terms into the decoder prompt. Keep the budget deliberately
    // small: a long prompt takes context away from the recorded audio.
    internal const int MaxVocabularyTerms = 10;
    internal const int MaxVocabularyCharacters = 96;

    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private OfflineRecognizer? _recognizer;
    private string? _modelKey;
    private int _operations;
    private bool _disposed;
    private bool _resourcesDisposed;

    public async Task<string> TranscribeAsync(
        FunAsrResolvedModel model,
        byte[] pcm16kMono,
        string language,
        IReadOnlyList<string> vocabulary,
        CancellationToken cancellationToken)
    {
        float[] samples = DecodePcm16(pcm16kMono);
        if (samples.Length == 0)
            return string.Empty;

        BeginOperation();
        bool entered = false;
        try
        {
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                OfflineRecognizer recognizer = GetOrCreateRecognizer(model);
                using OfflineStream stream = recognizer.CreateStream();
                string hotwords = FormatVocabulary(vocabulary);
                if (hotwords.Length > 0)
                    SetStreamOptionUtf8(stream, "hotwords", hotwords);
                // Do not force the Settings locale here. Qwen's language option is a hard
                // decoder instruction, not metadata, and forcing Chinese damages common
                // Chinese/English code-switching. An empty option lets the model detect it.
                _ = language;
                stream.AcceptWaveform(AudioCapture.TargetSampleRate, samples);
                recognizer.Decode(stream);
                cancellationToken.ThrowIfCancellationRequested();
                return stream.Result.Text ?? string.Empty;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
                _decodeGate.Release();
            EndOperation();
        }
    }

    public async Task WarmUpAsync(FunAsrResolvedModel model, CancellationToken cancellationToken)
    {
        BeginOperation();
        bool entered = false;
        try
        {
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            _ = await Task.Run(() => GetOrCreateRecognizer(model), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (entered)
                _decodeGate.Release();
            EndOperation();
        }
    }

    private OfflineRecognizer GetOrCreateRecognizer(FunAsrResolvedModel model)
    {
        ThrowIfDisposed();
        string key = ModelKey(model);
        if (_recognizer is not null && string.Equals(_modelKey, key, StringComparison.Ordinal))
            return _recognizer;

        _recognizer?.Dispose();
        // Clear the old identity before building the replacement. If native model loading
        // fails, a later request for the old model must not receive its disposed recognizer.
        _recognizer = null;
        _modelKey = null;
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.NumThreads = RecommendedThreadCount(Environment.ProcessorCount);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Qwen3Asr.ConvFrontend = SherpaPath(RequiredPath(model, "conv_frontend.onnx"));
        config.ModelConfig.Qwen3Asr.Encoder = SherpaPath(RequiredPath(model, "encoder.int8.onnx"));
        config.ModelConfig.Qwen3Asr.Decoder = SherpaPath(RequiredPath(model, "decoder.int8.onnx"));
        config.ModelConfig.Qwen3Asr.Tokenizer = Path.GetDirectoryName(
            SherpaPath(RequiredPath(model, "tokenizer_config.json")))!;
        config.ModelConfig.Qwen3Asr.MaxTotalLen = 512;
        config.ModelConfig.Qwen3Asr.MaxNewTokens = 128;
        config.ModelConfig.Qwen3Asr.Hotwords = string.Empty;

        _recognizer = new OfflineRecognizer(config);
        _modelKey = key;
        Log.Write(
            $"{model.Definition.DisplayName} recognizer loaded with {config.ModelConfig.NumThreads} CPU thread(s).");
        return _recognizer;
    }

    internal static int RecommendedThreadCount(int processorCount) =>
        Math.Clamp(processorCount, 1, 4);

    internal static string ModelKey(FunAsrResolvedModel model) =>
        string.Join('|', model.ArtifactPaths.OrderBy(item => item.Key).Select(item => item.Value));

    internal static string FormatVocabulary(IEnumerable<string> values)
    {
        var accepted = new List<string>();
        int characters = 0;
        foreach (string value in values.Take(MaxVocabularyTerms))
        {
            string term = value.Trim().Replace(',', ' ');
            if (term.Length == 0)
                continue;
            int added = term.Length + (accepted.Count == 0 ? 0 : 1);
            if (characters + added > MaxVocabularyCharacters)
                break;
            accepted.Add(term);
            characters += added;
        }
        return string.Join(',', accepted);
    }

    internal static float[] DecodePcm16(byte[] pcm)
    {
        var result = new float[pcm.Length / 2];
        for (int index = 0; index < result.Length; index++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2, 2));
            result[index] = sample / 32768f;
        }
        return result;
    }

    private static string RequiredPath(FunAsrResolvedModel model, string fileName) =>
        model.ArtifactPaths.Values.Single(path =>
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

    // sherpa-onnx 1.13.4 declares these strings as LPStr in its .NET wrapper. That corrupts
    // non-ASCII vocabulary on Windows, while the native tokenizer expects UTF-8.
    private static void SetStreamOptionUtf8(OfflineStream stream, string key, string value) =>
        SherpaOnnxOfflineStreamSetOption(stream.Handle, key, value);

    [DllImport("sherpa-onnx-c-api", EntryPoint = "SherpaOnnxOfflineStreamSetOption",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void SherpaOnnxOfflineStreamSetOption(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // Model paths in that wrapper have the same LPStr limitation. Existing files can be passed
    // through their ASCII DOS aliases, avoiding failures for non-ASCII Windows profile names.
    internal static string SherpaPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.All(character => character <= 0x7f))
            return path;

        var shortPath = new StringBuilder(32_768);
        uint length = GetShortPathName(path, shortPath, (uint)shortPath.Capacity);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Qwen3-ASR could not resolve a native-safe model path for '{path}'.");
        if (length >= shortPath.Capacity)
            throw new PathTooLongException("The Qwen3-ASR model path is too long for the native runtime.");

        string result = shortPath.ToString();
        if (result.Any(character => character > 0x7f))
        {
            throw new InvalidOperationException(
                "Qwen3-ASR currently requires an ASCII-compatible Windows model path. "
                + "Enable 8.3 short file names for the user volume or choose another local model.");
        }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder shortPath,
        uint bufferLength);

    public bool TryReset()
    {
        BeginOperation();
        bool entered = false;
        try
        {
            entered = _decodeGate.Wait(0);
            if (!entered)
                return false;
            _recognizer?.Dispose();
            _recognizer = null;
            _modelKey = null;
            return true;
        }
        finally
        {
            if (entered)
                _decodeGate.Release();
            EndOperation();
        }
    }

    public void Dispose()
    {
        bool cleanUp;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cleanUp = _operations == 0;
            if (cleanUp)
                _resourcesDisposed = true;
        }
        if (cleanUp)
            DisposeResources();
    }

    private void BeginOperation()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations++;
        }
    }

    private void EndOperation()
    {
        bool cleanUp = false;
        lock (_lifecycleGate)
        {
            _operations--;
            if (_disposed && _operations == 0 && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                cleanUp = true;
            }
        }
        if (cleanUp)
            DisposeResources();
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleGate)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void DisposeResources()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _modelKey = null;
        _decodeGate.Dispose();
    }
}
