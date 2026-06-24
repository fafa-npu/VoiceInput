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

    private WasapiCapture? _capture;
    private volatile bool _stopped = true;   // guards callbacks that race teardown
    private float _carry;                     // fractional sample position carried across buffers
    private float _lastSample;
    private float[] _mono = Array.Empty<float>();   // reused across callbacks (they are serialized)

    public const int TargetSampleRate = 16000;

    public void Start()
    {
        Stop();
        try
        {
            _capture = new WasapiCapture { ShareMode = AudioClientShareMode.Shared };
            _capture.DataAvailable += OnDataAvailable;
            _carry = 0f;
            _lastSample = 0f;
            _stopped = false;
            _capture.StartRecording();
            var f = _capture.WaveFormat;
            Log.Write($"Audio capture started: {f.SampleRate}Hz {f.Channels}ch {f.BitsPerSample}bit {f.Encoding}");
        }
        catch (Exception ex)
        {
            Log.Error("AudioCapture.Start", ex);
            throw;
        }
    }

    public void Stop()
    {
        _stopped = true;            // set first so in-flight callbacks bail out
        if (_capture is null) return;
        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch { /* device may already be gone */ }
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (_stopped || capture is null) return;

        var fmt = capture.WaveFormat;
        int channels = fmt.Channels;

        float[] mono = ToMonoFloat(e.Buffer, e.BytesRecorded, fmt, channels, out int frames);
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

    /// <summary>Decode to mono float into the reused <see cref="_mono"/> buffer (callbacks are serialized).</summary>
    private float[] ToMonoFloat(byte[] buffer, int bytes, WaveFormat fmt, int channels, out int frames)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        int bytesPerSample = isFloat ? 4 : 2;
        frames = bytes / (bytesPerSample * channels);
        if (frames == 0) return _mono;
        if (_mono.Length < frames) _mono = new float[frames];
        var mono = _mono;

        if (isFloat)
        {
            for (int f = 0; f < frames; f++)
            {
                float acc = 0;
                for (int c = 0; c < channels; c++)
                    acc += BitConverter.ToSingle(buffer, (f * channels + c) * 4);
                mono[f] = acc / channels;
            }
        }
        else
        {
            for (int f = 0; f < frames; f++)
            {
                int acc = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = (f * channels + c) * 2;
                    acc += (short)(buffer[o] | (buffer[o + 1] << 8));
                }
                mono[f] = acc / (channels * 32768f);
            }
        }
        return mono;
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

    public void Dispose() => Stop();
}
