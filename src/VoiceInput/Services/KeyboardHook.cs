using System.Runtime.InteropServices;
using System.Windows.Threading;
using VoiceInput.Interop;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Services;

/// <summary>
/// Global low-level keyboard and mouse hooks implementing chord-aware push-to-talk.
///
/// Design (per Win32 best practice): we do NOT suppress the push-to-talk key's events.
/// Instead we start capture on key-down, and if another key or a mouse button is chorded while
/// it is held (e.g. Ctrl+C or Ctrl+click) we cancel the gesture and let the input through
/// untouched. Only a clean press-and-release of the PTT key alone fires <see cref="Released"/>.
///
/// A watchdog timer recovers from two failure modes the hook alone can't: a missed key-up
/// (UAC prompt / lock screen / another hook swallowing it) that would otherwise wedge the
/// "held" state, and Windows silently dropping the hook on LowLevelHooksTimeout.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>PTT key pressed alone — begin dictation.</summary>
    public event Action? Engaged;
    /// <summary>PTT key released cleanly — finalize dictation.</summary>
    public event Action? Released;
    /// <summary>The watchdog observed that a missed key-up left the PTT key logically held.</summary>
    public event Action? RecoveredRelease;
    /// <summary>A chord was detected while PTT was held — abort dictation.</summary>
    public event Action? Cancelled;
    /// <summary>Escape pressed — requests cancelling the current dictation, if any.</summary>
    public event Action? EscapePressed;
    /// <summary>Enter pressed — used as a "done editing" signal to capture corrections.</summary>
    public event Action? Submitted;
    /// <summary>Alt+Shift+G pressed — requests switching to the other input profile.</summary>
    public event Action? ProfileSwitchRequested;

    // Kept alive in fields so the GC cannot collect callbacks while Win32 owns them.
    private readonly LowLevelKeyboardProc _proc;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly Func<int, short> _getAsyncKeyState;
    private IntPtr _hook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _moduleHandle = IntPtr.Zero;
    private DispatcherTimer? _watchdog;
    private int _idleTicks;
    private int _pttVk;
    private bool _pttDown;
    private bool _chorded;
    private bool _escapeDown;
    private bool _profileSwitchDown;

    private const int VkG = 0x47;

    public KeyboardHook(string pttKey)
        : this(pttKey, GetAsyncKeyState)
    {
    }

    internal KeyboardHook(string pttKey, Func<int, short> getAsyncKeyState)
    {
        _pttVk = ResolveVk(pttKey);
        _getAsyncKeyState = getAsyncKeyState;
        _proc = HookCallback;
        _mouseProc = MouseHookCallback;
    }

    internal bool IsPttGestureChorded => _pttDown && _chorded;

    public void SetPttKey(string pttKey)
    {
        int next = ResolveVk(pttKey);
        if (next == _pttVk)
            return;
        _pttVk = next;
        _pttDown = false;
        _chorded = false;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using (var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!)
            _moduleHandle = GetModuleHandle(mod.ModuleName);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, _moduleHandle, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook: " + Marshal.GetLastWin32Error());

        _mouseHook = SetWindowsMouseHookEx(WH_MOUSE_LL, _mouseProc, _moduleHandle, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            throw new InvalidOperationException("Failed to install mouse hook: " + error);
        }

        // Runs on the UI thread (where Install is called); GetAsyncKeyState polling and
        // re-installing the hook both require that context.
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _watchdog.Tick += Watchdog;
        _watchdog.Start();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HC_ACTION)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool injected = (data.flags & LLKHF_INJECTED) != 0 || data.dwExtraInfo == InjectionTag;
        if (injected)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

        ProcessKeyEvent((int)data.vkCode, isDown, isUp);

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            ProcessMouseEvent((int)wParam, data.flags, data.dwExtraInfo);
        }

        // Ctrl+click must retain its normal foreground-app behavior.
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    internal void ProcessMouseEvent(int message, uint flags = 0, IntPtr extraInfo = default)
    {
        bool injected = (flags & LLMHF_INJECTED) != 0 || extraInfo == InjectionTag;
        if (injected || message is not (WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN))
            return;

        if (_pttDown && !_chorded)
        {
            _chorded = true;
            Cancelled?.Invoke();
        }
    }

    internal void ProcessKeyEvent(int vkCode, bool isDown, bool isUp)
    {
        bool isPtt = vkCode == _pttVk;

        if (vkCode == VK_ESCAPE)
        {
            if (isUp)
            {
                _escapeDown = false;
                return;
            }

            if (!isDown || _escapeDown)
                return;

            _escapeDown = true;
            // Escape owns cancellation for this gesture. Mark the PTT as chorded so its later
            // release cannot also stop/finalize the session through the normal gesture path.
            if (_pttDown && !_chorded)
                _chorded = true;
            EscapePressed?.Invoke();
            return;
        }

        if (isDown && vkCode == VK_RETURN)
            Submitted?.Invoke();

        if (vkCode == VkG && isUp)
        {
            _profileSwitchDown = false;
            return;
        }

        if (vkCode == VkG && isDown && IsHeld(VK_MENU) && IsHeld(VK_SHIFT))
        {
            if (_profileSwitchDown)
                return;
            _profileSwitchDown = true;
            if (_pttDown && !_chorded)
            {
                _chorded = true;
                Cancelled?.Invoke();
            }
            ProfileSwitchRequested?.Invoke();
            return;
        }

        if (isDown)
        {
            if (isPtt)
            {
                if (!_pttDown)
                {
                    _pttDown = true;
                    _chorded = HasOtherHeldInput();
                    if (!_chorded)
                        Engaged?.Invoke();
                }
                // else: auto-repeat key-down while held — ignore.
            }
            else if (_pttDown && !_chorded)
            {
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

    }

    private bool IsHeld(int vkCode) => (_getAsyncKeyState(vkCode) & 0x8000) != 0;

    private bool HasOtherHeldInput()
    {
        if (IsHeld(VK_LBUTTON)
            || IsHeld(VK_RBUTTON)
            || IsHeld(VK_MBUTTON)
            || IsHeld(VK_XBUTTON1)
            || IsHeld(VK_XBUTTON2))
        {
            return true;
        }

        int genericPttVk = GenericModifierFor(_pttVk);
        for (int vkCode = 0x08; vkCode <= 0xFE; vkCode++)
        {
            if (vkCode == _pttVk || vkCode == genericPttVk)
                continue;

            if ((_getAsyncKeyState(vkCode) & 0x8000) != 0)
                return true;
        }

        return false;
    }

    private static int GenericModifierFor(int vkCode) => vkCode switch
    {
        VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
        VK_RSHIFT => VK_SHIFT,
        VK_RMENU => VK_MENU,
        _ => vkCode,
    };

    private void Watchdog(object? sender, EventArgs e)
    {
        ReconcileKeyState();
        if (_pttDown)
        {
            _idleTicks = 0;
        }
        else if (++_idleTicks >= 50)   // ~5s idle: re-assert the hook in case Windows silently dropped it
        {
            _idleTicks = 0;
            Reinstall();
        }
    }

    internal void ReconcileKeyState()
    {
        if (_escapeDown && !IsHeld(VK_ESCAPE))
            _escapeDown = false;
        if (_profileSwitchDown && !IsHeld(VkG))
            _profileSwitchDown = false;
        if (!_pttDown || (_getAsyncKeyState(_pttVk) & 0x8000) != 0)
            return;

        // Keep recovered releases distinct so the controller can handle them immediately instead
        // of letting a stale release queue behind a newer physical gesture.
        bool fireRecoveredRelease = !_chorded;
        _pttDown = false;
        _chorded = false;
        if (fireRecoveredRelease)
            RecoveredRelease?.Invoke();   // chorded sessions were already cancelled
    }

    private void Reinstall()
    {
        var freshKeyboard = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, _moduleHandle, 0);
        if (freshKeyboard == IntPtr.Zero)
            return;   // keep the existing hooks if re-registration failed

        var freshMouse = SetWindowsMouseHookEx(WH_MOUSE_LL, _mouseProc, _moduleHandle, 0);
        if (freshMouse == IntPtr.Zero)
        {
            UnhookWindowsHookEx(freshKeyboard);
            return;
        }

        var oldKeyboard = _hook;
        var oldMouse = _mouseHook;
        _hook = freshKeyboard;
        _mouseHook = freshMouse;
        if (oldKeyboard != IntPtr.Zero) UnhookWindowsHookEx(oldKeyboard);
        if (oldMouse != IntPtr.Zero) UnhookWindowsHookEx(oldMouse);
    }

    private static int ResolveVk(string key) => key switch
    {
        "RightCtrl" => VK_RCONTROL,
        "LeftCtrl" => VK_LCONTROL,
        "RightAlt" => VK_RMENU,
        "RightShift" => VK_RSHIFT,
        "CapsLock" => VK_CAPITAL,
        _ => VK_RCONTROL,
    };

    public void Dispose()
    {
        _watchdog?.Stop();
        _watchdog = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
