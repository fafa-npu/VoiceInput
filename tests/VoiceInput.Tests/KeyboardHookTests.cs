using VoiceInput.Services;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Tests;

public sealed class KeyboardHookTests
{
    [Fact]
    public void MissedKeyUpRecoveryKeepsTheNextPressIndependent()
    {
        using var hook = new KeyboardHook("LeftCtrl", _ => 0);
        var events = CaptureEvents(hook);
        hook.RecoveredRelease += () => events.Add("recovered");

        hook.ProcessKeyEvent(VK_LCONTROL, isDown: true, isUp: false);
        hook.ReconcileKeyState();
        hook.ProcessKeyEvent(VK_LCONTROL, isDown: true, isUp: false);
        hook.ProcessKeyEvent(VK_LCONTROL, isDown: false, isUp: true);

        Assert.Equal(["engaged", "recovered", "engaged", "released"], events);
    }

    [Fact]
    public void ChordedMissedKeyUpDoesNotRaiseRecoveredRelease()
    {
        using var hook = new KeyboardHook("LeftCtrl", _ => 0);
        var events = CaptureEvents(hook);
        hook.RecoveredRelease += () => events.Add("recovered");

        hook.ProcessKeyEvent(VK_LCONTROL, isDown: true, isUp: false);
        hook.ProcessKeyEvent(VK_V, isDown: true, isUp: false);
        hook.ReconcileKeyState();
        hook.ProcessKeyEvent(VK_LCONTROL, isDown: true, isUp: false);
        hook.ProcessKeyEvent(VK_LCONTROL, isDown: false, isUp: true);

        Assert.Equal(["engaged", "cancelled", "engaged", "released"], events);
    }

    [Fact]
    public void IgnoresPttGestureWhenAnotherKeyWasAlreadyHeld()
    {
        var heldKeys = new HashSet<int> { VK_V };
        using var hook = new KeyboardHook("RightCtrl", KeyStateFrom(heldKeys));
        var events = CaptureEvents(hook);

        hook.ProcessKeyEvent(VK_RCONTROL, isDown: true, isUp: false);
        Assert.True(hook.IsPttGestureChorded);
        heldKeys.Remove(VK_V);
        hook.ProcessKeyEvent(VK_RCONTROL, isDown: false, isUp: true);

        Assert.Empty(events);
        Assert.False(hook.IsPttGestureChorded);
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

    [Fact]
    public void ProfileSwitchShortcutFiresOncePerChord()
    {
        var heldKeys = new HashSet<int> { VK_MENU, VK_SHIFT };
        using var hook = new KeyboardHook("RightCtrl", KeyStateFrom(heldKeys));
        int switches = 0;
        hook.ProfileSwitchRequested += () => switches++;

        hook.ProcessKeyEvent(0x47, isDown: true, isUp: false);
        hook.ProcessKeyEvent(0x47, isDown: true, isUp: false);
        hook.ProcessKeyEvent(0x47, isDown: false, isUp: true);
        hook.ProcessKeyEvent(0x47, isDown: true, isUp: false);

        Assert.Equal(2, switches);
    }

    [Fact]
    public void ProfileSwitchShortcutCancelsAnActivePttGesture()
    {
        var heldKeys = new HashSet<int>();
        using var hook = new KeyboardHook("RightCtrl", KeyStateFrom(heldKeys));
        var events = CaptureEvents(hook);
        hook.ProfileSwitchRequested += () => events.Add("switch");

        heldKeys.Add(VK_RCONTROL);
        hook.ProcessKeyEvent(VK_RCONTROL, isDown: true, isUp: false);
        heldKeys.Add(VK_MENU);
        heldKeys.Add(VK_SHIFT);
        hook.ProcessKeyEvent(0x47, isDown: true, isUp: false);

        Assert.Equal(["engaged", "cancelled", "switch"], events);
    }

    [Fact]
    public void MissedProfileShortcutKeyUpIsRecovered()
    {
        var heldKeys = new HashSet<int> { VK_MENU, VK_SHIFT };
        using var hook = new KeyboardHook("RightCtrl", KeyStateFrom(heldKeys));
        int switches = 0;
        hook.ProfileSwitchRequested += () => switches++;

        hook.ProcessKeyEvent(0x47, isDown: true, isUp: false);
        hook.ReconcileKeyState();
        hook.ProcessKeyEvent(0x47, isDown: true, isUp: false);

        Assert.Equal(2, switches);
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
