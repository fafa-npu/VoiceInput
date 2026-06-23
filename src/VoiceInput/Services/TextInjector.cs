using System.Runtime.InteropServices;
using System.Windows;
using VoiceInput.Interop;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Services;

/// <summary>
/// Injects text into the focused control using clipboard + simulated Ctrl+V.
/// Before pasting it temporarily closes the foreground window's IME (so a CJK IME in native
/// mode can't swallow Ctrl+V), then restores the IME state and the previous clipboard text.
/// Must be called on the STA UI thread (clipboard access requires it).
/// </summary>
public sealed class TextInjector
{
    public async Task InjectAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        IntPtr hwnd = GetForegroundWindow();

        // Preserve the user's clipboard text (v1 preserves plain text; images/files are not restored).
        string? previousText = TryGetClipboardText();

        // Temporarily close the IME on the target window.
        IntPtr himc = hwnd != IntPtr.Zero ? ImmGetContext(hwnd) : IntPtr.Zero;
        bool imeWasOpen = false;
        if (himc != IntPtr.Zero)
        {
            imeWasOpen = ImmGetOpenStatus(himc);
            if (imeWasOpen) ImmSetOpenStatus(himc, false);
        }

        try
        {
            SetClipboardText(text);
            await Task.Delay(30);          // let the clipboard settle before pasting
            SendCtrlV();
            await Task.Delay(130);          // let the target app consume the paste before we restore
        }
        finally
        {
            if (himc != IntPtr.Zero)
            {
                if (imeWasOpen) ImmSetOpenStatus(himc, true);
                ImmReleaseContext(hwnd, himc);
            }

            // Restore the previous clipboard.
            if (previousText is not null) SetClipboardText(previousText);
            else TryClearClipboard();
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        inputs[0] = KeyScan(SCAN_LCONTROL, up: false);
        inputs[1] = KeyScan(SCAN_V, up: false);
        inputs[2] = KeyScan(SCAN_V, up: true);
        inputs[3] = KeyScan(SCAN_LCONTROL, up: true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyScan(ushort scan, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = scan,
                dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = InjectionTag,   // so our own keyboard hook ignores this
            }
        }
    };

    private static string? TryGetClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { Thread.Sleep(40); }
        }
        return null;
    }

    private static void SetClipboardText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch { Thread.Sleep(40); }
        }
    }

    private static void TryClearClipboard()
    {
        try { Clipboard.Clear(); } catch { }
    }
}
