using System.IO;

namespace VoiceInput.Services;

internal static class PcmWave
{
    public static byte[] Wrap(byte[] pcm, int sampleRate)
    {
        const int bits = 16;
        const int channels = 1;
        int byteRate = sampleRate * channels * bits / 8;
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)(channels * bits / 8));
        writer.Write((short)bits);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }
}
