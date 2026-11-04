using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// The batch yield as a ring: the good share in the OK colour from the top clockwise, the bad
/// share in the NG colour, so the split reads without the number too. Below 0 (not known yet)
/// only the empty track is drawn. Colours come from the tokens and follow the theme.
/// </summary>
public sealed class YieldRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<YieldRing, double>(nameof(Value), -1);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<YieldRing, double>(nameof(Thickness), 10);

    static YieldRing()
    {
        AffectsRender<YieldRing>(ValueProperty, ThicknessProperty);
    }

    /// <summary>Yield in percent, 0 to 100; below 0 when not known.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness * 2) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - Thickness) / 2;
        var value = Value;
        if (value < 0)
        {
            context.DrawEllipse(null, new Pen(Brush("Vs.Brush.Border"), Thickness), centre, radius, radius);
            return;
        }
        var good = Math.Clamp(value, 0, 100) / 100;
        if (good < 1) DrawArc(context, Brush("Vs.Brush.Status.Ng"), centre, radius, good, 1);
        if (good > 0) DrawArc(context, Brush("Vs.Brush.Status.Ok"), centre, radius, 0, good);
    }

    /// <summary>An arc from one share of the turn to another, clockwise from 12 o'clock.</summary>
    private void DrawArc(DrawingContext context, IBrush brush, Point centre, double radius, double from, double to)
    {
        var pen = new Pen(brush, Thickness, lineCap: PenLineCap.Flat);
        if (to - from >= 0.9999)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }
        Point At(double share) => new(centre.X + radius * Math.Sin(share * 2 * Math.PI),
            centre.Y - radius * Math.Cos(share * 2 * Math.PI));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(from), false);
            ctx.ArcTo(At(to), new Size(radius, radius), 0, to - from > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
