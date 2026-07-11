using NAudio.Wave;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class AudioSampleConverterTests
{
    [Fact]
    public void DecodesStereoPcm16ToMono()
    {
        byte[] bytes = [0x00, 0x40, 0x00, 0xC0];
        float[] reusable = [];

        float[] mono = AudioSampleConverter.DecodeMono(
            bytes, bytes.Length, new WaveFormat(48000, 16, 2), ref reusable, out int frames);

        Assert.Equal(1, frames);
        Assert.Equal(0f, mono[0], precision: 5);
    }

    [Fact]
    public void DecodesUnsignedPcm8()
    {
        byte[] bytes = [0, 128, 255];
        float[] reusable = [];

        float[] mono = AudioSampleConverter.DecodeMono(
            bytes, bytes.Length, new WaveFormat(48000, 8, 1), ref reusable, out int frames);

        Assert.Equal(3, frames);
        Assert.Equal(-1f, mono[0]);
        Assert.Equal(0f, mono[1]);
        Assert.InRange(mono[2], 0.99f, 1f);
    }

    [Fact]
    public void IgnoresIncompleteTrailingFrame()
    {
        byte[] bytes = [0, 0, 0];
        float[] reusable = [];

        _ = AudioSampleConverter.DecodeMono(
            bytes, bytes.Length, new WaveFormat(48000, 16, 1), ref reusable, out int frames);

        Assert.Equal(1, frames);
    }

    [Fact]
    public void DecodesSignedPcm24()
    {
        byte[] bytes = [0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80];
        float[] reusable = [];

        float[] mono = AudioSampleConverter.DecodeMono(
            bytes, bytes.Length, new WaveFormat(48000, 24, 1), ref reusable, out int frames);

        Assert.Equal(2, frames);
        Assert.InRange(mono[0], 0.9999f, 1f);
        Assert.Equal(-1f, mono[1]);
    }

    [Fact]
    public void DecodesSignedPcm32()
    {
        byte[] bytes = BitConverter.GetBytes(int.MinValue).Concat(BitConverter.GetBytes(int.MaxValue)).ToArray();
        float[] reusable = [];

        float[] mono = AudioSampleConverter.DecodeMono(
            bytes, bytes.Length, new WaveFormat(48000, 32, 1), ref reusable, out int frames);

        Assert.Equal(2, frames);
        Assert.Equal(-1f, mono[0]);
        Assert.InRange(mono[1], 0.9999f, 1f);
    }

    [Fact]
    public void DecodesIeeeFloatAndSanitizesNonFiniteValues()
    {
        byte[] bytes = BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(float.NaN)).ToArray();
        float[] reusable = [];

        float[] mono = AudioSampleConverter.DecodeMono(
            bytes, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), ref reusable, out int frames);

        Assert.Equal(2, frames);
        Assert.Equal(0.25f, mono[0]);
        Assert.Equal(0f, mono[1]);
    }
}
