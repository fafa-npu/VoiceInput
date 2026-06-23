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
    private float _carry;        // fractional sample position carried across resample buffers
    private float _lastSample;

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
        var fmt = _capture!.WaveFormat;
        int channels = fmt.Channels;

        // Decode to a mono float buffer regardless of source encoding.
        float[] mono = ToMonoFloat(e.Buffer, e.BytesRecorded, fmt, channels, out int frames);
        if (frames == 0) return;

        // RMS for the meter.
        double sum = 0;
        for (int i = 0; i < frames; i++) sum += mono[i] * mono[i];
        float rms = (float)Math.Sqrt(sum / frames);
        LevelChanged?.Invoke(Math.Clamp(rms * 5.0f, 0f, 1f));

        // Resample to 16 kHz mono int16 for the recognizer push stream.
        if (PcmChunkAvailable is not null)
        {
            byte[] pcm = ResampleTo16kPcm16(mono, frames, fmt.SampleRate);
            if (pcm.Length > 0) PcmChunkAvailable.Invoke(pcm);
        }
    }

    private static float[] ToMonoFloat(byte[] buffer, int bytes, WaveFormat fmt, int channels, out int frames)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int total = bytes / 4;
            frames = total / channels;
            var mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float acc = 0;
                for (int c = 0; c < channels; c++)
                    acc += BitConverter.ToSingle(buffer, (f * channels + c) * 4);
                mono[f] = acc / channels;
            }
            return mono;
        }

        // Assume 16-bit PCM otherwise.
        int totalS = bytes / 2;
        frames = totalS / channels;
        var monoP = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
                acc += (short)(buffer[(f * channels + c) * 2] | (buffer[(f * channels + c) * 2 + 1] << 8));
            monoP[f] = acc / (channels * 32768f);
        }
        return monoP;
    }

    /// <summary>Simple linear-interpolation downsample to 16 kHz, then to little-endian int16 bytes.</summary>
    private byte[] ResampleTo16kPcm16(float[] mono, int frames, int sourceRate)
    {
        if (sourceRate == TargetSampleRate)
        {
            var direct = new byte[frames * 2];
            for (int i = 0; i < frames; i++) WriteSample(direct, i * 2, mono[i]);
            return direct;
        }

        double step = (double)sourceRate / TargetSampleRate;
        var outSamples = new List<byte>(capacity: (int)(frames / step) * 2 + 4);
        double pos = _carry;
        while (pos < frames)
        {
            int idx = (int)pos;
            float frac = (float)(pos - idx);
            float a = idx == 0 ? _lastSample : mono[idx - 1];
            float b = mono[idx];
            float sample = a + (b - a) * frac;
            int offset = outSamples.Count;
            outSamples.Add(0); outSamples.Add(0);
            var tmp = new byte[2];
            WriteSample(tmp, 0, sample);
            outSamples[offset] = tmp[0];
            outSamples[offset + 1] = tmp[1];
            pos += step;
        }
        _carry = (float)(pos - frames);        // keep sub-sample phase for the next buffer
        _lastSample = mono[frames - 1];
        return outSamples.ToArray();
    }

    private static void WriteSample(byte[] dst, int offset, float sample)
    {
        short s = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
        dst[offset] = (byte)(s & 0xFF);
        dst[offset + 1] = (byte)((s >> 8) & 0xFF);
    }

    public void Dispose() => Stop();
}
