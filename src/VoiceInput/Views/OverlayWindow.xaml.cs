using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoiceInput.Models;
using static VoiceInput.Interop.NativeMethods;

namespace VoiceInput.Views;

/// <summary>
/// Borderless, non-activating capsule overlay. Shows the live waveform and the
/// running transcript while dictating. The host window is a fixed-size transparent layer; the
/// capsule is centered and its Width is animated (0.25s) so it grows/shrinks smoothly from the
/// center as the transcript changes — never the instant jump that SizeToContent produces.
/// Never takes focus (WS_EX_NOACTIVATE) and stays out of the taskbar / Alt+Tab (WS_EX_TOOLWINDOW).
/// </summary>
public partial class OverlayWindow : Window
{
    private const double EdgeMargin = 64;

    // Width-geometry constants (must match the XAML module and information-pod chrome).
    private const double WaveModuleWidth = 64;
    private const double InformationPodChrome = 16 + 54 + 12; // left pad + live indicator + right pad
    private const double TextMaxWidth = 580;      // matches Label.MaxWidth
    private const double MinCapsuleWidth = 220;
    private const double MaxCapsuleWidth = WaveModuleWidth + InformationPodChrome + TextMaxWidth;

    private const double WidthAnimSeconds = 0.25;
    private const double EntranceSeconds = 0.35;
    private const double ExitSeconds = 0.22;

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);
    private readonly DispatcherTimer _profileNoticeTimer;

    // The window we anchor the overlay to (the app being dictated into); captured at show time
    // so the capsule appears on whichever monitor the user is actually working on.
    private IntPtr _anchorWindow;

    public OverlayWindow()
    {
        InitializeComponent();
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        Capsule.RenderTransform = group;
        Capsule.RenderTransformOrigin = new Point(0.5, 1.0);
        _profileNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _profileNoticeTimer.Tick += (_, _) => HideAnimated();
    }

    public OverlayPosition Position { get; set; } = OverlayPosition.Bottom;

    /// <summary>Pulled by the waveform each frame to read the latest level.</summary>
    public Func<double>? LevelSource
    {
        get => Wave.LevelSource;
        set => Wave.LevelSource = value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    public void ShowListening(string placeholder)
    {
        _profileNoticeTimer.Stop();
        // Capture the active window now (before we Show, which never steals focus) so the overlay
        // lands on the monitor the user is working on.
        _anchorWindow = GetForegroundWindow();

        PhaseLabel.Text = "LIVE INPUT";
        LiveIndicator.Visibility = Visibility.Visible;
        Label.Text = placeholder;
        Label.Opacity = 0.65;
        SetWidth(ComputeTargetWidth(placeholder), animate: false);   // entrance scales from this width
        Wave.Start();
        Opacity = 0;
        Show();
        Reposition();
        PlayEntrance();
    }

    public void ShowProfileChanged(string profileName, string activationSummary)
    {
        _anchorWindow = GetForegroundWindow();
        _profileNoticeTimer.Stop();
        PhaseLabel.Text = "INPUT PROFILE";
        LiveIndicator.Visibility = Visibility.Collapsed;
        Label.Text = $"{profileName} · {activationSummary}";
        Label.Opacity = 1.0;
        SetWidth(ComputeTargetWidth(Label.Text), animate: false);
        Wave.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (!IsVisible)
            Show();
        Reposition();
        PlayEntrance();
        _profileNoticeTimer.Start();
    }

    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        PhaseLabel.Text = "LIVE INPUT";
        LiveIndicator.Visibility = Visibility.Visible;
        Label.Opacity = 1.0;
        // When the transcript is longer than the capsule can show, display the tail (the latest
        // words) with a leading ellipsis instead of clipping the end out of view.
        string shown = FitTail(text, TextMaxWidth);
        Label.Text = shown;
        SetWidth(ComputeTargetWidth(shown), animate: true);
    }

    /// <summary>Return the longest trailing slice of <paramref name="text"/> (prefixed with "…")
    /// whose rendered width fits within <paramref name="maxWidth"/>. Returns the text unchanged when it fits.</summary>
    private string FitTail(string text, double maxWidth)
    {
        if (MeasureTextWidth(text) <= maxWidth) return text;
        const string ellipsis = "…";
        double ellipsisWidth = MeasureTextWidth(ellipsis);

        // Binary-search the smallest number of leading chars to drop so the remainder fits.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ellipsisWidth + MeasureTextWidth(text[mid..]) <= maxWidth) hi = mid;
            else lo = mid + 1;
        }
        return ellipsis + text[lo..];
    }

    public void SetStatus(string status)
    {
        PhaseLabel.Text = "PROCESSING";
        LiveIndicator.Visibility = Visibility.Collapsed;
        Label.Opacity = 0.85;
        string shown = FitTail(status, TextMaxWidth);
        Label.Text = shown;
        SetWidth(ComputeTargetWidth(shown), animate: true);
    }

    public void HideAnimated()
    {
        _profileNoticeTimer.Stop();
        Wave.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromSeconds(ExitSeconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => Hide();
        var shrink = new DoubleAnimation(1.0, 0.9, TimeSpan.FromSeconds(ExitSeconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Animate (or set) the capsule width. A fresh animation with no explicit From
    /// eases from the current value, so rapid partial-result updates chase the latest target
    /// smoothly instead of snapping.</summary>
    private void SetWidth(double target, bool animate)
    {
        if (animate)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromSeconds(WidthAnimSeconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Capsule.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            Capsule.BeginAnimation(WidthProperty, null);   // drop any running animation
            Capsule.Width = target;
        }
    }

    private double ComputeTargetWidth(string text)
    {
        double textWidth = Math.Min(MeasureTextWidth(text), TextMaxWidth);
        double content = WaveModuleWidth + InformationPodChrome + textWidth + 2; // +2 guards rounding
        return Math.Clamp(content, MinCapsuleWidth, MaxCapsuleWidth);
    }

    private double MeasureTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var typeface = new Typeface(Label.FontFamily, Label.FontStyle, Label.FontWeight, Label.FontStretch);
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Label.FontSize,
            Brushes.White,
            pixelsPerDip: VisualTreeHelper.GetDpi(Label).PixelsPerDip);
        return ft.Width;
    }

    private void PlayEntrance()
    {
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var scaleAnim = new DoubleAnimation(0.85, 1.0, TimeSpan.FromSeconds(EntranceSeconds)) { EasingFunction = spring };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        var rise = new DoubleAnimation(14, 0, TimeSpan.FromSeconds(EntranceSeconds)) { EasingFunction = spring };
        _translate.BeginAnimation(TranslateTransform.YProperty, rise);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)));
    }

    /// <summary>
    /// Position the capsule top- or bottom-center on the active monitor (the one holding the foreground
    /// window, falling back to the cursor's monitor). Uses physical pixels via SetWindowPos so it
    /// works across monitors with different DPI scaling — SystemParameters.WorkArea only covers
    /// the primary screen.
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        IntPtr hmon = _anchorWindow != IntPtr.Zero
            ? MonitorFromWindow(_anchorWindow, MONITOR_DEFAULTTONEAREST)
            : (GetCursorPos(out var pt) ? MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero);
        if (hmon == IntPtr.Zero) return;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi)) return;

        double scale = 1.0;
        if (GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        int physW = (int)Math.Round(Width * scale);
        int physH = (int)Math.Round(Height * scale);
        var work = mi.rcWork;   // physical pixels
        int x = work.left + ((work.right - work.left) - physW) / 2;
        int y = CalculateVerticalPosition(Position, work.top, work.bottom, physH, scale);

        SetWindowPos(hwnd, IntPtr.Zero, x, y, physW, physH, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    internal static int CalculateVerticalPosition(
        OverlayPosition position,
        int workTop,
        int workBottom,
        int physicalHeight,
        double scale)
    {
        int margin = (int)Math.Round(EdgeMargin * scale);
        return position == OverlayPosition.Top
            ? workTop + margin
            : workBottom - physicalHeight - margin;
    }
}
