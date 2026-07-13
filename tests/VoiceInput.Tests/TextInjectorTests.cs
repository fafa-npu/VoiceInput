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

    [Theory]
    [InlineData(0, "", false)]
    [InlineData(1, "", true)]
    [InlineData(0, "uia-id", true)]
    public void RecognizesUsableFocusedControlIdentity(long focusedControl, string controlId, bool expected) =>
        Assert.Equal(expected, TextInjector.HasUsableControlIdentity(new IntPtr(focusedControl), controlId));
}
