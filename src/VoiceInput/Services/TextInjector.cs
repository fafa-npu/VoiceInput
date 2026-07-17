using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
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
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public sealed record Target(IntPtr Window, uint ProcessId, string WindowClass, IntPtr FocusedControl, string ControlId);
    public sealed record Result(bool Success, int CharactersInserted = 0, string? Error = null);

    public Target CaptureTarget()
    {
        IntPtr window = GetForegroundWindow();
        uint threadId = GetWindowThreadProcessId(window, out uint processId);
        var className = new StringBuilder(256);
        _ = GetClassName(window, className, className.Capacity);
        return new Target(window, processId, className.ToString(), FocusedControlWindow(threadId), FocusedControlId());
    }

    public Task<Result> InjectAsync(string text, Target target)
    {
        if (string.IsNullOrEmpty(text)) return Task.FromResult(new Result(true));
        if (!MatchesCurrentTarget(target))
            return Task.FromResult(new Result(false, 0, "The focused window or input control changed while gujiguji was processing."));

        var inputs = new INPUT[2];
        for (int i = 0; i < text.Length; i++)
        {
            if (!MatchesCurrentTarget(target))
                return Task.FromResult(new Result(false, i, "The focused window or input control changed during insertion."));
            inputs[0] = UnicodeKey(text[i], up: false);
            inputs[1] = UnicodeKey(text[i], up: true);
            uint sent = SendInput(2, inputs, InputSize);
            if (sent != 2)
                return Task.FromResult(new Result(false, CompletedCharacters(i, sent),
                    $"Windows accepted only {sent} of 2 keyboard events (error {Marshal.GetLastWin32Error()})."));
        }
        return Task.FromResult(new Result(true, text.Length));
    }

    public bool IsCurrentTarget(Target target) => MatchesCurrentTarget(target);

    internal static int CompletedCharacters(int charactersBeforeCurrent, uint eventsSent) =>
        eventsSent == 2 ? charactersBeforeCurrent + 1 : charactersBeforeCurrent;

    private static bool MatchesCurrentTarget(Target target)
    {
        IntPtr window = GetForegroundWindow();
        if (window != target.Window) return false;
        uint threadId = GetWindowThreadProcessId(window, out uint processId);
        if (processId != target.ProcessId) return false;
        var className = new StringBuilder(256);
        _ = GetClassName(window, className, className.Capacity);
        IntPtr focusedControl = FocusedControlWindow(threadId);
        string controlId = FocusedControlId();
        return HasUsableControlIdentity(target.FocusedControl, target.ControlId) &&
            HasUsableControlIdentity(focusedControl, controlId) &&
            className.ToString() == target.WindowClass &&
            focusedControl == target.FocusedControl &&
            controlId == target.ControlId;
    }

    internal static bool HasUsableControlIdentity(IntPtr focusedControl, string controlId) =>
        focusedControl != IntPtr.Zero || !string.IsNullOrEmpty(controlId);

    private static IntPtr FocusedControlWindow(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    private static string FocusedControlId()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return string.Empty;
            int[] runtimeId = focused.GetRuntimeId();
            return $"{focused.Current.ProcessId}:{focused.Current.AutomationId}:{string.Join('.', runtimeId)}";
        }
        catch
        {
            return string.Empty;
        }
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
