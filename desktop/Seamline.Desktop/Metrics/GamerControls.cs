using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Seamline.Desktop.Metrics;

/// <summary>
/// Medidor em arco (270°) com brilho neon, para o uso da CPU e da GPU. O valor vai de 0 a 100 e
/// anda suave até o novo número (<see cref="AnimateTo"/>).
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(ArcGauge), new FrameworkPropertyMetadata(Colors.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(ArcGauge), new FrameworkPropertyMetadata(Colors.Magenta, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Color StartColor
    {
        get => (Color)GetValue(StartColorProperty);
        set => SetValue(StartColorProperty, value);
    }

    public Color EndColor
    {
        get => (Color)GetValue(EndColorProperty);
        set => SetValue(EndColorProperty, value);
    }

    public double Thickness { get; set; } = 12;

    public void AnimateTo(double value) =>
        BeginAnimation(ValueProperty, new DoubleAnimation(Math.Clamp(value, 0, 100), TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    private const double StartAngle = 135; // começa embaixo à esquerda
    private const double Sweep = 270;

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness * 2) / 2;

        // Trilho apagado e marquinhas a cada 10%.
        var track = new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x2E)), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, radius, StartAngle, Sweep));
        var tick = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x8A, 0x93, 0xB0)), 1.5);
        for (var i = 0; i <= 10; i++)
        {
            var angle = (StartAngle + Sweep * i / 10) * Math.PI / 180;
            var inner = radius - Thickness - 4;
            var outer = radius - Thickness - (i % 5 == 0 ? 12 : 8);
            dc.DrawLine(tick, new Point(center.X + inner * Math.Cos(angle), center.Y + inner * Math.Sin(angle)),
                new Point(center.X + outer * Math.Cos(angle), center.Y + outer * Math.Sin(angle)));
        }

        var value = Math.Clamp(Value, 0, 100);
        if (value < 0.5)
            return;
        var arc = Arc(center, radius, StartAngle, Sweep * value / 100);
        var gradient = new LinearGradientBrush(StartColor, EndColor, new Point(0, 1), new Point(1, 0));
        // Brilho: o mesmo arco, mais grosso e transparente, por baixo.
        var glow = new LinearGradientBrush(Color.FromArgb(0x40, StartColor.R, StartColor.G, StartColor.B),
            Color.FromArgb(0x40, EndColor.R, EndColor.G, EndColor.B), new Point(0, 1), new Point(1, 0));
        dc.DrawGeometry(null, new Pen(glow, Thickness * 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        dc.DrawGeometry(null, new Pen(gradient, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }

    private static Geometry Arc(Point center, double radius, double start, double sweep)
    {
        sweep = Math.Min(sweep, 359.9);
        Point At(double degrees)
        {
            var r = degrees * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(r), center.Y + radius * Math.Sin(r));
        }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(start), false, false);
            ctx.ArcTo(At(start + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Gráfico de linha do histórico (FPS dos últimos 60 s), com área em degradê embaixo.</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly List<double> _values = [];

    public int Capacity { get; set; } = 60;
    public Color LineColor { get; set; } = Color.FromRgb(0x7C, 0xFF, 0x4F);

    public void Push(double value)
    {
        _values.Add(value);
        if (_values.Count > Capacity)
            _values.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        // Linhas de grade bem apagadas.
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0x8A, 0x93, 0xB0)), 1);
        for (var i = 1; i < 4; i++)
            dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        if (_values.Count < 2 || w <= 0 || h <= 0)
            return;

        var max = Math.Max(30, _values.Max() * 1.15);
        var step = w / (Capacity - 1);
        var offset = (Capacity - _values.Count) * step;
        Point P(int i) => new(offset + i * step, h - 2 - (h - 4) * Math.Clamp(_values[i] / max, 0, 1));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < _values.Count; i++)
                ctx.LineTo(P(i), true, true);
        }
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(offset, h), true, true);
            for (var i = 0; i < _values.Count; i++)
                ctx.LineTo(P(i), true, true);
            ctx.LineTo(new Point(offset + (_values.Count - 1) * step, h), true, true);
        }
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, LineColor.R, LineColor.G, LineColor.B),
            Color.FromArgb(0x00, LineColor.R, LineColor.G, LineColor.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x50, LineColor.R, LineColor.G, LineColor.B)), 5) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(LineColor), 2) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Cores das temperaturas e das barras: verde-água tranquilo, amarelo atenção, vermelho quente.</summary>
public static class HeatColors
{
    public static readonly Color Cool = Color.FromRgb(0x00, 0xE5, 0xFF);
    public static readonly Color Warm = Color.FromRgb(0xFF, 0xC4, 0x00);
    public static readonly Color Hot = Color.FromRgb(0xFF, 0x3B, 0x5C);

    public static Color ForTemperature(double celsius) => celsius >= 85 ? Hot : celsius >= 70 ? Warm : Cool;
    public static Color ForPercent(double percent) => percent >= 90 ? Hot : percent >= 75 ? Warm : Cool;
}
