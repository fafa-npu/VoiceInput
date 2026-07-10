using NAudio.Dmo;
using NAudio.Wave;

namespace VoiceInput.Services;

internal static class AudioSampleConverter
{
    public static float[] DecodeMono(byte[] buffer, int bytes, WaveFormat format, ref float[] reusable, out int frames)
    {
        int channels = format.Channels;
        int bits = format.BitsPerSample;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible pcmExtensible && pcmExtensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
        if ((!isFloat && !isPcm) || channels <= 0 || bits is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"Unsupported capture format: {format}");

        int bytesPerSample = bits / 8;
        frames = bytes / (bytesPerSample * channels);
        if (reusable.Length < frames) reusable = new float[frames];

        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * bytesPerSample;
                sum += DecodeSample(buffer, offset, bits, isFloat);
            }
            reusable[frame] = Math.Clamp(sum / channels, -1f, 1f);
        }
        return reusable;
    }

    private static float DecodeSample(byte[] buffer, int offset, int bits, bool isFloat)
    {
        if (isFloat)
        {
            if (bits != 32) throw new NotSupportedException("Only 32-bit IEEE float capture is supported.");
            float value = BitConverter.ToSingle(buffer, offset);
            return float.IsFinite(value) ? value : 0f;
        }

        return bits switch
        {
            8 => (buffer[offset] - 128) / 128f,
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            24 => ReadInt24(buffer, offset) / 8388608f,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => throw new NotSupportedException(),
        };
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        int value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }
}
