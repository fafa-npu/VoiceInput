using VoiceInput.Services;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Tests;

public sealed class KeyboardHookTests
{
    [Fact]
    public void IgnoresPttGestureWhenAnotherKeyWasAlreadyHeld()
    {
        var heldKeys = new HashSet<int> { VK_V };
        using var hook = new KeyboardHook("RightCtrl", KeyStateFrom(heldKeys));
        var events = CaptureEvents(hook);

        hook.ProcessKeyEvent(VK_RCONTROL, isDown: true, isUp: false);
        heldKeys.Remove(VK_V);
        hook.ProcessKeyEvent(VK_RCONTROL, isDown: false, isUp: true);

        Assert.Empty(events);
    }

    [Fact]
    public void CancelsGestureWhenAnotherKeyIsPressedAfterPtt()
    {
        using var hook = new KeyboardHook("RightCtrl", _ => 0);
        var events = CaptureEvents(hook);

        hook.ProcessKeyEvent(VK_RCONTROL, isDown: true, isUp: false);
        hook.ProcessKeyEvent(VK_V, isDown: true, isUp: false);
        hook.ProcessKeyEvent(VK_RCONTROL, isDown: false, isUp: true);

        Assert.Equal(["engaged", "cancelled"], events);
    }

    [Theory]
    [InlineData("RightCtrl", VK_RCONTROL, VK_CONTROL)]
    [InlineData("LeftCtrl", VK_LCONTROL, VK_CONTROL)]
    [InlineData("RightAlt", VK_RMENU, VK_MENU)]
    [InlineData("RightShift", VK_RSHIFT, VK_SHIFT)]
    [InlineData("CapsLock", VK_CAPITAL, VK_CAPITAL)]
    public void PttAndItsGenericAliasDoNotCountAsAChord(
        string pttKey,
        int pttVk,
        int genericPttVk)
    {
        var heldKeys = new HashSet<int> { pttVk, genericPttVk };
        using var hook = new KeyboardHook(pttKey, KeyStateFrom(heldKeys));
        var events = CaptureEvents(hook);

        hook.ProcessKeyEvent(pttVk, isDown: true, isUp: false);
        heldKeys.Clear();
        hook.ProcessKeyEvent(pttVk, isDown: false, isUp: true);

        Assert.Equal(["engaged", "released"], events);
    }

    private static Func<int, short> KeyStateFrom(HashSet<int> heldKeys) =>
        vkCode => heldKeys.Contains(vkCode) ? unchecked((short)0x8000) : (short)0;

    private static List<string> CaptureEvents(KeyboardHook hook)
    {
        var events = new List<string>();
        hook.Engaged += () => events.Add("engaged");
        hook.Released += () => events.Add("released");
        hook.Cancelled += () => events.Add("cancelled");
        return events;
    }
}
