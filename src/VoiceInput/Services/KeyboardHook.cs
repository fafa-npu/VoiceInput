using System.Runtime.InteropServices;
using VoiceInput.Interop;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Services;

/// <summary>
/// Global low-level keyboard hook implementing chord-aware push-to-talk.
///
/// Design (per Win32 best practice): we do NOT suppress the push-to-talk key's events.
/// Instead we start capture on key-down, and if another key is chorded while it is held
/// (e.g. Right-Ctrl+C) we cancel the gesture and let the chord through untouched. Only a
/// clean press-and-release of the PTT key alone fires <see cref="Released"/>.
///
/// The callback stays trivial (set flags, raise events) to respect the LowLevelHooksTimeout
/// cap; consumers marshal to the UI thread themselves.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>PTT key pressed alone — begin dictation.</summary>
    public event Action? Engaged;
    /// <summary>PTT key released cleanly — finalize dictation.</summary>
    public event Action? Released;
    /// <summary>A chord was detected while PTT was held — abort dictation.</summary>
    public event Action? Cancelled;

    private readonly LowLevelKeyboardProc _proc;   // kept alive in a field so the GC can't collect it
    private IntPtr _hook = IntPtr.Zero;
    private int _pttVk;
    private bool _pttDown;
    private bool _chorded;

    public KeyboardHook(string pttKey)
    {
        _pttVk = ResolveVk(pttKey);
        _proc = HookCallback;
    }

    public void SetPttKey(string pttKey) => _pttVk = ResolveVk(pttKey);

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook: " + Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HC_ACTION)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Ignore our own injected Ctrl+V so we don't trigger on it.
        bool injected = (data.flags & LLKHF_INJECTED) != 0 || data.dwExtraInfo == InjectionTag;
        if (injected)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        bool isPtt = (int)data.vkCode == _pttVk;

        if (isDown)
        {
            if (isPtt)
            {
                if (!_pttDown)
                {
                    _pttDown = true;
                    _chorded = false;
                    Engaged?.Invoke();
                }
                // else: auto-repeat key-down while held — ignore.
            }
            else if (_pttDown && !_chorded)
            {
                // Another key chorded while PTT held -> this is a shortcut, not dictation.
                _chorded = true;
                Cancelled?.Invoke();
            }
        }
        else if (isUp && isPtt && _pttDown)
        {
            _pttDown = false;
            if (!_chorded)
                Released?.Invoke();
            _chorded = false;
        }

        // Never suppress: keep system shortcuts intact for the default PTT key.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static int ResolveVk(string key) => key switch
    {
        "RightCtrl" => VK_RCONTROL,
        "RightAlt" => VK_RMENU,
        "RightShift" => VK_RSHIFT,
        "CapsLock" => VK_CAPITAL,
        _ => VK_RCONTROL,
    };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
