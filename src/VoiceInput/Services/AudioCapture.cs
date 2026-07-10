using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceInput.Services;

/// <summary>
/// Captures the default microphone once via WASAPI and fans the data out to two consumers:
///   1. <see cref="LevelChanged"/> — a normalized 0..1 RMS level driving the waveform.
///   2. <see cref="PcmChunkAvailable"/> — 16 kHz / 16-bit / mono PCM for the Azure push stream.
/// Capturing once and fanning out keeps the meter and recognizer perfectly in sync.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public event Action<float>? LevelChanged;
    public event Action<byte[]>? PcmChunkAvailable;

    private readonly object _sync = new();
    private WasapiCapture? _capture;
    private volatile bool _sessionActive;     // true while a dictation is buffering (PCM + level delivered)
    private volatile bool _deviceLive;        // true once the device has delivered its first audio buffer
    private TaskCompletionSource<bool> _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _graceCts;   // pending warm-window release
    private float _carry;                     // fractional sample position carried across buffers
    private float _lastSample;
    private float[] _mono = Array.Empty<float>();   // reused across callbacks (they are serialized)

    public const int TargetSampleRate = 16000;

    /// <summary>How long the mic stays warm after a dictation so back-to-back dictation is instant.</summary>
    public const int WarmGraceMs = 60000;

    /// <summary>Completes when the device delivers its first audio buffer in the current session
    /// (already complete if the mic is still warm from a recent dictation).</summary>
    public Task FirstFrame => _firstFrame.Task;

    /// <summary>
    /// Begin a dictation: open the mic (or reuse one still warm from a recent session) and start
    /// delivering audio. Cold-start warm-up is reported via <see cref="FirstFrame"/>.
    /// </summary>
    public void BeginSession()
    {
        lock (_sync)
        {
            _graceCts?.Cancel();
            _graceCts = null;
            _carry = 0f;
            _lastSample = 0f;
            _firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_capture is null)
            {
                _deviceLive = false;
                StartDeviceLocked();
            }
            else if (_deviceLive)
            {
                _firstFrame.TrySetResult(true);   // already warm — ready immediately
            }
            _sessionActive = true;
        }
    }

    /// <summary>End the dictation but keep the mic warm for <see cref="WarmGraceMs"/> so a quick
    /// follow-up dictation doesn't pay the cold-start latency. Released after the grace window.</summary>
    public void EndSession()
    {
        lock (_sync)
        {
            _sessionActive = false;
            _graceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _graceCts = cts;
            Task.Delay(WarmGraceMs, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_sync)
                {
                    if (_graceCts == cts && !_sessionActive) StopDeviceLocked();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Fully stop the mic now (privacy): used on pause and shutdown.</summary>
    public void Release()
    {
        lock (_sync)
        {
            _graceCts?.Cancel();
            _graceCts = null;
            _sessionActive = false;
            StopDeviceLocked();
        }
    }

    private void StartDeviceLocked()
    {
        WasapiCapture? cap = null;
        try
        {
            cap = new WasapiCapture { ShareMode = AudioClientShareMode.Shared };
            cap.DataAvailable += OnDataAvailable;
            cap.StartRecording();
            _capture = cap;
            var f = cap.WaveFormat;
            Log.Write($"Audio capture started: {f.SampleRate}Hz {f.Channels}ch {f.BitsPerSample}bit {f.Encoding}");
        }
        catch (Exception ex)
        {
            try { cap?.Dispose(); } catch { }
            _capture = null;
            Log.Error("AudioCapture.StartDevice", ex);
            throw;
        }
    }

    private void StopDeviceLocked()
    {
        var cap = _capture;
        _capture = null;
        _deviceLive = false;
        if (cap is null) return;
        try
        {
            cap.DataAvailable -= OnDataAvailable;
            cap.StopRecording();
            cap.Dispose();
        }
        catch { /* device may already be gone */ }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null) return;

        if (e.BytesRecorded > 0 && !_deviceLive)
        {
            _deviceLive = true;
            _firstFrame.TrySetResult(true);   // device is delivering audio — safe to invite speech
        }
        if (!_sessionActive) return;          // warm but idle: keep the device alive, discard audio

        var fmt = capture.WaveFormat;
        float[] mono;
        int frames;
        try
        {
            mono = AudioSampleConverter.DecodeMono(e.Buffer, e.BytesRecorded, fmt, ref _mono, out frames);
        }
        catch (NotSupportedException ex)
        {
            Log.Error("AudioCapture format", ex);
            Release();
            return;
        }
        if (frames == 0) return;

        double sum = 0;
        for (int i = 0; i < frames; i++) sum += mono[i] * mono[i];
        float rms = (float)Math.Sqrt(sum / frames);
        LevelChanged?.Invoke(Math.Clamp(rms * 5.0f, 0f, 1f));

        if (PcmChunkAvailable is not null)
        {
            byte[] pcm = ResampleTo16kPcm16(mono, frames, fmt.SampleRate);
            if (pcm.Length > 0) PcmChunkAvailable.Invoke(pcm);
        }
    }

    /// <summary>Linear-interpolation downsample to 16 kHz int16, allocation-light (one output array).</summary>
    private byte[] ResampleTo16kPcm16(float[] mono, int frames, int sourceRate)
    {
        if (sourceRate == TargetSampleRate)
        {
            var direct = new byte[frames * 2];
            for (int i = 0; i < frames; i++) WriteSample(direct, i * 2, mono[i]);
            return direct;
        }

        double step = (double)sourceRate / TargetSampleRate;
        var outBuf = new byte[((int)(frames / step) + 2) * 2];
        int outIdx = 0;
        double pos = _carry;
        while (pos < frames)
        {
            int idx = (int)pos;
            float frac = (float)(pos - idx);
            float a = idx == 0 ? _lastSample : mono[idx - 1];
            float b = mono[idx];
            float sample = a + (b - a) * frac;
            if (outIdx + 2 > outBuf.Length) Array.Resize(ref outBuf, outBuf.Length + 64);
            WriteSample(outBuf, outIdx, sample);
            outIdx += 2;
            pos += step;
        }
        _carry = (float)(pos - frames);
        _lastSample = mono[frames - 1];
        return outIdx == outBuf.Length ? outBuf : outBuf[..outIdx];
    }

    private static void WriteSample(byte[] dst, int offset, float sample)
    {
        short s = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
        dst[offset] = (byte)(s & 0xFF);
        dst[offset + 1] = (byte)((s >> 8) & 0xFF);
    }

    public void Dispose() => Release();
}
