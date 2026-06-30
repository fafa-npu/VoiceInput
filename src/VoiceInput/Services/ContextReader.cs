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

    /// <summary>Read ONLY the focused input box's value (for capturing what the user edited).
    /// Skips documents/editors/terminals — unlike <see cref="TryReadAsync"/> it does not fall back to
    /// a window's text buffer, so it won't return a whole editor/terminal as if it were an input.</summary>
    public async Task<string?> TryReadFocusedValueAsync()
    {
        try
        {
            var task = Task.Run(ReadFocusedValue);
            var done = await Task.WhenAny(task, Task.Delay(TimeoutMs));
            return done == task ? await task : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFocusedValue()
    {
        AutomationElement? e;
        try { e = AutomationElement.FocusedElement; } catch { return null; }
        if (e is null) return null;

        try
        {
            if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(v) && !IsUnreadable(v)) return v;
            }
        }
        catch { }
        try
        {
            // TextPattern only if it's small enough to be an input — a document/editor/terminal
            // returns the whole buffer, which is not "what the user edited". ponytail: 600 chars / 6 lines.
            if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var t = ((TextPattern)tp).DocumentRange.GetText(800)?.Trim();
                if (!string.IsNullOrWhiteSpace(t) && !IsUnreadable(t) && t.Length < 600 && t.Count(c => c == '\n') < 6)
                    return t;
            }
        }
        catch { }
        return null;
    }

    private static bool IsUnreadable(string t) =>
        t.Contains("not accessible at this time", StringComparison.OrdinalIgnoreCase);

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
        // VS Code / some Electron apps expose this placeholder when the editor isn't UIA-readable —
        // it's not real content, so treat it as "couldn't read" (fall back to no context).
        if (text.Contains("not accessible at this time", StringComparison.OrdinalIgnoreCase)) return null;
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
