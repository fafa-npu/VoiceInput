using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using VoiceInput.Models;

namespace VoiceInput.Services;

internal sealed record FunAsrProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class FunAsrEngine : ISpeechEngine
{
    private readonly FunAsrResolvedModel _model;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<FunAsrProcessResult>> _processRunner;
    private readonly MemoryStream _buffer = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _processCancellation;
    private Task<FunAsrProcessResult>? _runningProcess;
    private string? _temporaryWavePath;
    private bool _closed = true;
    private bool _canceled;
    private bool _disposed;

    public FunAsrEngine(FunAsrResolvedModel model)
        : this(model, FunAsrProcess.RunAsync)
    {
    }

    internal FunAsrEngine(
        FunAsrResolvedModel model,
        Func<ProcessStartInfo, CancellationToken, Task<FunAsrProcessResult>> processRunner)
    {
        _model = model;
        _processRunner = processRunner;
    }

    public bool NeedsAudioFeed => true;
    public bool HasInterimResults => false;
    public int StopTimeoutMs => 60_000;

#pragma warning disable CS0067
    public event Action<string>? Partial;
#pragma warning restore CS0067
    public event Action<string>? Final;
    public event Action<SpeechFault>? Fault;

    public Task StartAsync(string language)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _buffer.SetLength(0);
            _closed = false;
            _canceled = false;
        }
        return Task.CompletedTask;
    }

    public void Feed(byte[] pcm16kMono)
    {
        lock (_gate)
        {
            if (_closed || _canceled || _disposed)
                return;
            _buffer.Write(pcm16kMono, 0, pcm16kMono.Length);
        }
    }

    public async Task StopAsync()
    {
        byte[] pcm;
        lock (_gate)
        {
            if (_closed || _canceled || _disposed)
                return;
            _closed = true;
            pcm = _buffer.ToArray();
        }
        if (pcm.Length == 0)
            return;

        using var processCancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _processCancellation = processCancellation;
            if (_canceled)
                processCancellation.Cancel();
        }

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "VoiceInput");
        string wavePath = Path.Combine(temporaryDirectory, $"funasr-{Guid.NewGuid():N}.wav");
        Task<FunAsrProcessResult>? runningProcess = null;
        lock (_gate)
            _temporaryWavePath = wavePath;
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await File.WriteAllBytesAsync(
                wavePath,
                PcmWave.Wrap(pcm, AudioCapture.TargetSampleRate),
                processCancellation.Token);

            ProcessStartInfo startInfo = FunAsrProcess.CreateStartInfo(_model, wavePath);
            runningProcess = _processRunner(startInfo, processCancellation.Token);
            lock (_gate)
            {
                _runningProcess = runningProcess;
                if (_canceled || _disposed)
                    processCancellation.Cancel();
            }
            FunAsrProcessResult result = await runningProcess;
            if (processCancellation.IsCancellationRequested || IsCanceled())
                return;

            if (result.ExitCode != 0)
            {
                string detail = $"Native process exited with exit code {result.ExitCode}.";
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                    detail += $" {Truncate(result.StandardError.Trim(), 500)}";
                Log.Write($"FunAsrEngine failed: {detail}");
                Fault?.Invoke(new(
                    SpeechFaultKind.Service,
                    "Local FunASR transcription failed. Check the selected model in Setup.",
                    detail));
                return;
            }

            string transcript = result.StandardOutput.Trim();
            if (transcript.Length > 0)
                Final?.Invoke(transcript);
        }
        catch (OperationCanceledException) when (processCancellation.IsCancellationRequested || IsCanceled())
        {
        }
        catch (Exception exception)
        {
            Log.Error("FunAsrEngine.StopAsync", exception);
            Fault?.Invoke(new(
                SpeechFaultKind.Service,
                "Local FunASR transcription failed. Check the selected model in Setup.",
                exception.Message));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_processCancellation, processCancellation))
                    _processCancellation = null;
                if (ReferenceEquals(_runningProcess, runningProcess))
                    _runningProcess = null;
                if (_temporaryWavePath == wavePath)
                    _temporaryWavePath = null;
            }
            TryDeleteWave(wavePath);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _canceled = true;
            cancellation = _processCancellation;
        }
        cancellation?.Cancel();
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        Task<FunAsrProcessResult>? runningProcess;
        string? wavePath;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _closed = true;
            _canceled = true;
            cancellation = _processCancellation;
            runningProcess = _runningProcess;
            wavePath = _temporaryWavePath;
        }
        cancellation?.Cancel();
        if (runningProcess is not null)
        {
            try { runningProcess.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException exception) when (exception.InnerExceptions.All(
                inner => inner is OperationCanceledException))
            {
            }
            catch (AggregateException exception)
            {
                Log.Error("FunAsrEngine.Dispose", exception.Flatten().InnerException ?? exception);
            }
        }
        if (wavePath is not null)
            TryDeleteWave(wavePath);
        _buffer.Dispose();
    }

    private bool IsCanceled()
    {
        lock (_gate)
            return _canceled;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";

    private static void TryDeleteWave(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Write($"FunAsrEngine could not remove temporary audio: {exception.Message}");
        }
    }
}

internal static class FunAsrProcess
{
    public static ProcessStartInfo CreateStartInfo(FunAsrResolvedModel resolved, string wavePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = resolved.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(resolved.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        IReadOnlyList<string> modelPaths = resolved.ArtifactPaths.Values.ToList();
        if (resolved.Definition.Runner == FunAsrRunnerKind.Nano)
        {
            string encoder = modelPaths.Single(path =>
                Path.GetFileName(path).Contains("encoder", StringComparison.OrdinalIgnoreCase));
            string decoder = modelPaths.Single(path =>
                !string.Equals(path, encoder, StringComparison.OrdinalIgnoreCase));
            AddArguments(startInfo, "--enc", encoder, "-m", decoder);
        }
        else
        {
            AddArguments(startInfo, "-m", modelPaths.Single());
        }

        AddArguments(startInfo, "-a", wavePath, "--vad", resolved.VadPath);
        return startInfo;
    }

    public static async Task<FunAsrProcessResult> RunAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The FunASR runtime could not be started.");
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!), process);

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            throw;
        }

        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }
}
