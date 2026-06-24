using System.Drawing;
using System.Drawing.Drawing2D;
using VoiceInput.Interop;

namespace VoiceInput.Services;

/// <summary>
/// Builds the tray icon at runtime — a simple white microphone on a blue rounded square,
/// so no .ico asset is needed and it stays legible on both light and dark taskbars.
/// </summary>
internal static class TrayIconFactory
{
    public static Icon CreateMicIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float s = size;

            // Rounded-square background (visible on any taskbar theme).
            var bg = new RectangleF(0.5f, 0.5f, s - 1, s - 1);
            using (var path = Rounded(bg, s * 0.24f))
            using (var brush = new LinearGradientBrush(bg,
                       ColorTranslator.FromHtml("#5B7CFA"),
                       ColorTranslator.FromHtml("#2D54D8"),
                       LinearGradientMode.Vertical))
                g.FillPath(brush, path);

            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, Math.Max(1.4f, s * 0.06f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            // Mic body (vertical pill).
            float bw = s * 0.30f, bh = s * 0.38f;
            using (var body = Rounded(new RectangleF((s - bw) / 2f, s * 0.16f, bw, bh), bw / 2f))
                g.FillPath(white, body);

            // U-shaped holder arc + stem + base.
            float arcW = s * 0.48f, arcH = s * 0.48f;
            g.DrawArc(pen, (s - arcW) / 2f, s * 0.30f, arcW, arcH, 25, 130);
            float cx = s / 2f;
            g.DrawLine(pen, cx, s * 0.74f, cx, s * 0.84f);
            g.DrawLine(pen, cx - s * 0.12f, s * 0.85f, cx + s * 0.12f, s * 0.85f);
        }

        // Clone() detaches a managed copy so we can free the live GDI handle immediately.
        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
