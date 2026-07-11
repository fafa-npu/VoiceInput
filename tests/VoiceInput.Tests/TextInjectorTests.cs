using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class TextInjectorTests
{
    [Theory]
    [InlineData(0u, 4)]
    [InlineData(1u, 4)]
    [InlineData(2u, 5)]
    public void CountsCharacterOnlyAfterBothKeyboardEvents(uint eventsSent, int expected) =>
        Assert.Equal(expected, TextInjector.CompletedCharacters(4, eventsSent));
}
