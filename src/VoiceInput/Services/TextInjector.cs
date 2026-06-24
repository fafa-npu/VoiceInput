using System.Runtime.InteropServices;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Services;

/// <summary>
/// Injects text into the focused control by synthesizing the characters directly via
/// SendInput + KEYEVENTF_UNICODE (each char becomes a VK_PACKET → WM_CHAR). This works in
/// Chromium/Electron apps (Microsoft 365 Copilot, Teams) where simulated Ctrl+V can miss because
/// they read the clipboard asynchronously, and it touches neither the clipboard nor the IME.
/// </summary>
public sealed class TextInjector
{
    public Task InjectAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        // Two INPUTs (down + up) per UTF-16 code unit. Surrogate pairs are sent as their two
        // code units, which is exactly what KEYEVENTF_UNICODE expects.
        var inputs = new INPUT[text.Length * 2];
        int i = 0;
        foreach (char c in text)
        {
            inputs[i++] = UnicodeKey(c, up: false);
            inputs[i++] = UnicodeKey(c, up: true);
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return Task.CompletedTask;
    }

    private static INPUT UnicodeKey(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = InjectionTag,   // so our own keyboard hook ignores these
            }
        }
    };
}
