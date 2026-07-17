using VoiceInput.Controls;

namespace VoiceInput.Tests;

public sealed class WaveformControlTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.1, 0.2)]
    [InlineData(0.5, 1.0)]
    [InlineData(-0.1, 0.0)]
    [InlineData(1.1, 1.0)]
    public void ApplyVisualGainDoublesAndClampsLevel(double level, double expected)
    {
        Assert.Equal(expected, WaveformControl.ApplyVisualGain(level), precision: 10);
    }
}
