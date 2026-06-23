using System.Windows;
using System.Windows.Media;

namespace VoiceInput.Controls;

/// <summary>
/// Five vertical bars driven by the live audio RMS level. Weighted [0.5,0.8,1.0,0.75,0.55]
/// for a natural middle-high shape, with a smooth attack/release envelope and ±4% per-bar
/// jitter for organic motion. The level is pulled from <see cref="LevelSource"/> each frame so
/// the audio thread never has to marshal onto the UI thread.
/// </summary>
public sealed class WaveformControl : FrameworkElement
{
    private const int BarCount = 5;
    private static readonly double[] Weights = { 0.5, 0.8, 1.0, 0.75, 0.55 };
    private const double Attack = 0.40;
    private const double Release = 0.15;

    private readonly double[] _current = new double[BarCount];
    private readonly Random _rng = new();
    private readonly Brush _brush;
    private bool _running;

    /// <summary>Pulled each render frame; returns the current 0..1 level. Set by the owner.</summary>
    public Func<double>? LevelSource { get; set; }

    public WaveformControl()
    {
        Width = 44;
        Height = 32;
        _brush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2));
        _brush.Freeze();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        Array.Clear(_current);
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double level = Math.Clamp(LevelSource?.Invoke() ?? 0, 0, 1);
        for (int i = 0; i < BarCount; i++)
        {
            double jitter = 1.0 + (_rng.NextDouble() * 0.08 - 0.04);   // ±4%
            double target = Math.Clamp(level * Weights[i] * jitter, 0, 1);
            double rate = target > _current[i] ? Attack : Release;
            _current[i] += (target - _current[i]) * rate;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        const double gap = 3.0;
        double barW = (w - gap * (BarCount - 1)) / BarCount;
        double radius = barW / 2;
        double minH = h * 0.14;

        for (int i = 0; i < BarCount; i++)
        {
            double bh = minH + (h - minH) * _current[i];
            double x = i * (barW + gap);
            double y = (h - bh) / 2;          // grow symmetrically from the vertical center
            dc.DrawRoundedRectangle(_brush, null, new Rect(x, y, barW, bh), radius, radius);
        }
    }
}
