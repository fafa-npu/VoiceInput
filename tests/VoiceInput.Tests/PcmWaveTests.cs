using System.Text;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class PcmWaveTests
{
    [Fact]
    public void WrapCreatesCanonicalMonoPcmWave()
    {
        byte[] wav = PcmWave.Wrap([1, 2, 3, 4], 16000);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(48, wav.Length);
        Assert.Equal(4, BitConverter.ToInt32(wav, 40));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, wav[44..]);
    }
}
