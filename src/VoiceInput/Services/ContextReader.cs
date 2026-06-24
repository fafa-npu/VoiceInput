using System.Collections.Generic;
using System.Windows.Automation;

namespace VoiceInput.Services;

/// <summary>
/// Best-effort read of the text around the user's cursor via UI Automation: the focused
/// control's value, or the foreground window's text area (e.g. a terminal buffer). Used to give
/// the LLM refinement step context. Bounded in size and time; returns null when nothing is readable
/// (e.g. apps like VS Code whose editors don't expose UIA text).
/// </summary>
public sealed class ContextReader
{
    private const int MaxChars = 1500;
    private const int TimeoutMs = 1500;
    private const int MaxNodes = 250;

    public async Task<string?> TryReadAsync()
    {
        try
        {
            var task = Task.Run(ReadInternal);     // UIA off the UI thread; cross-process calls can be slow
            var done = await Task.WhenAny(task, Task.Delay(TimeoutMs));
            return done == task ? await task : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadInternal()
    {
        string? text = null;
        try { text = ReadElementText(AutomationElement.FocusedElement); } catch { }

        if (string.IsNullOrWhiteSpace(text))
        {
            IntPtr hwnd = Interop.NativeMethods.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    text = ReadElementText(root) ?? FindTextInSubtree(root);
                }
                catch { }
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length > MaxChars ? text[^MaxChars..] : text;   // keep the most recent tail
    }

    private static string? ReadElementText(AutomationElement? e)
    {
        if (e is null) return null;
        try
        {
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }
        try
        {
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var t = ((TextPattern)tp).DocumentRange.GetText(MaxChars + 200);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        catch { }
        return null;
    }

    private static string? FindTextInSubtree(AutomationElement root)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        int n = 0;
        while (queue.Count > 0 && n < MaxNodes)
        {
            var e = queue.Dequeue();
            n++;
            try
            {
                if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                {
                    var t = ((TextPattern)tp).DocumentRange.GetText(MaxChars + 200);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
            catch { }
            try
            {
                var c = walker.GetFirstChild(e);
                while (c != null) { queue.Enqueue(c); c = walker.GetNextSibling(c); }
            }
            catch { }
        }
        return null;
    }
}
