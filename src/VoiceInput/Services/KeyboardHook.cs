using System.Runtime.InteropServices;
using System.Windows.Threading;
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
    /// <summary>A chord was detected while PTT was held — abort dictation.</summary>
    public event Action? Cancelled;
    /// <summary>Enter pressed — used as a "done editing" signal to capture corrections.</summary>
    public event Action? Submitted;

    private readonly LowLevelKeyboardProc _proc;   // kept alive in a field so the GC can't collect it
    private IntPtr _hook = IntPtr.Zero;
    private IntPtr _moduleHandle = IntPtr.Zero;
    private DispatcherTimer? _watchdog;
    private int _idleTicks;
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
        using (var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!)
            _moduleHandle = GetModuleHandle(mod.ModuleName);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, _moduleHandle, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook: " + Marshal.GetLastWin32Error());

        // Runs on the UI thread (where Install is called); GetAsyncKeyState polling and
        // re-installing the hook both require that context.
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
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
        bool isPtt = (int)data.vkCode == _pttVk;

        if (isDown && (int)data.vkCode == VK_RETURN)
            Submitted?.Invoke();

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

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Watchdog(object? sender, EventArgs e)
    {
        if (_pttDown)
        {
            // Reconcile a key-up the hook never saw (secure desktop, focus loss, another hook).
            bool physicallyDown = (GetAsyncKeyState(_pttVk) & 0x8000) != 0;
            if (!physicallyDown)
            {
                bool fireReleased = !_chorded;
                _pttDown = false;
                _chorded = false;
                if (fireReleased) Released?.Invoke();   // chorded sessions were already cancelled
            }
            _idleTicks = 0;
        }
        else if (++_idleTicks >= 10)   // ~5s idle: re-assert the hook in case Windows silently dropped it
        {
            _idleTicks = 0;
            Reinstall();
        }
    }

    private void Reinstall()
    {
        var fresh = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, _moduleHandle, 0);
        if (fresh == IntPtr.Zero) return;   // keep the existing hook if re-registration failed
        var old = _hook;
        _hook = fresh;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
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
    }
}
