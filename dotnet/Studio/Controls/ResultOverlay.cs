using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// Draws the runtime's result graphics (vector data in image pixels) over a live image shown
/// with Stretch="Uniform" in the same cell: each check outlined in its status colour with its
/// shape and its label, e.g. "✗ Code" (UI-UX-SPEC.md 4.1). Colours come from the tokens.
/// Points are in JPEG pixels (the contract's LiveFrame). A tap anywhere on it raises
/// <see cref="ImageTapped"/> with the image point; the view-model answers with
/// <see cref="TooltipText"/> at <see cref="TooltipAt"/>, drawn as a bubble above that point.
/// </summary>
public sealed class ResultOverlay : Control, ICustomHitTest
{
    public static readonly StyledProperty<string?> TooltipTextProperty =
        AvaloniaProperty.Register<ResultOverlay, string?>(nameof(TooltipText));

    public static readonly StyledProperty<Point2?> TooltipAtProperty =
        AvaloniaProperty.Register<ResultOverlay, Point2?>(nameof(TooltipAt));

    public static readonly StyledProperty<IReadOnlyList<ResultGraphic>?> GraphicsProperty =
        AvaloniaProperty.Register<ResultOverlay, IReadOnlyList<ResultGraphic>?>(nameof(Graphics));

    public static readonly StyledProperty<double> ImageWidthProperty =
        AvaloniaProperty.Register<ResultOverlay, double>(nameof(ImageWidth), 1);

    public static readonly StyledProperty<double> ImageHeightProperty =
        AvaloniaProperty.Register<ResultOverlay, double>(nameof(ImageHeight), 1);

    static ResultOverlay()
    {
        AffectsRender<ResultOverlay>(GraphicsProperty, ImageWidthProperty, ImageHeightProperty, TooltipTextProperty,
            TooltipAtProperty);
    }

    public ResultOverlay()
    {
        Tapped += (_, e) =>
        {
            var (scale, ox, oy) = Fit(Bounds.Size, ImageWidth, ImageHeight);
            if (scale <= 0) return;
            var p = e.GetPosition(this);
            ImageTapped?.Invoke(new Point2((p.X - ox) / scale, (p.Y - oy) / scale), TapSlop / scale);
        };
    }

    /// <summary>How far from a line or cross a finger may land, in screen pixels.</summary>
    public const double TapSlop = 24;

    /// <summary>A tap: the point and the slop, both in image pixels.</summary>
    public event Action<Point2, double>? ImageTapped;

    public bool HitTest(Point point) => true;

    public string? TooltipText
    {
        get => GetValue(TooltipTextProperty);
        set => SetValue(TooltipTextProperty, value);
    }

    public Point2? TooltipAt
    {
        get => GetValue(TooltipAtProperty);
        set => SetValue(TooltipAtProperty, value);
    }

    public IReadOnlyList<ResultGraphic>? Graphics
    {
        get => GetValue(GraphicsProperty);
        set => SetValue(GraphicsProperty, value);
    }

    public double ImageWidth
    {
        get => GetValue(ImageWidthProperty);
        set => SetValue(ImageWidthProperty, value);
    }

    public double ImageHeight
    {
        get => GetValue(ImageHeightProperty);
        set => SetValue(ImageHeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    /// <summary>Where an image of this size lands in a box with Stretch="Uniform", centred.</summary>
    public static (double Scale, double X, double Y) Fit(Size box, double width, double height)
    {
        var scale = Math.Min(box.Width / width, box.Height / height);
        return (scale, (box.Width - width * scale) / 2, (box.Height - height * scale) / 2);
    }

    public override void Render(DrawingContext context)
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return;
        var (scale, ox, oy) = Fit(Bounds.Size, ImageWidth, ImageHeight);
        DrawGraphics(context, scale, ox, oy);
        if (TooltipText is { Length: > 0 } tip && TooltipAt is { } at) DrawTooltip(context, tip, new Point(ox + at.X * scale, oy + at.Y * scale));
    }

    /// <summary>A bubble in the raised surface colour with the text, above the point (below it near the top).</summary>
    private void DrawTooltip(DrawingContext context, string text, Point at)
    {
        var fontSize = Resource<double>("Vs.Font.Title", 24);
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize,
            Resource<IBrush>("Vs.Brush.Text", Brushes.White))
        {
            MaxTextWidth = Math.Max(120, Math.Min(480, Bounds.Width - 32)),
        };
        const double pad = 12, gap = 16;
        var size = new Size(formatted.Width + pad * 2, formatted.Height + pad * 2);
        var x = Math.Clamp(at.X - size.Width / 2, 4, Math.Max(4, Bounds.Width - size.Width - 4));
        var y = at.Y - gap - size.Height;
        if (y < 4) y = Math.Min(at.Y + gap, Math.Max(4, Bounds.Height - size.Height - 4));
        var box = new Rect(new Point(x, y), size);
        context.DrawRectangle(Resource<IBrush>("Vs.Brush.SurfaceRaised", Brushes.Black),
            new Pen(Resource<IBrush>("Vs.Brush.Border", Brushes.Gray), 2), box, 8, 8);
        context.DrawText(formatted, new Point(box.X + pad, box.Y + pad));
        // the point tapped
        context.DrawEllipse(Resource<IBrush>("Vs.Brush.Text", Brushes.White), null, at, 5, 5);
    }

    private void DrawGraphics(DrawingContext context, double scale, double ox, double oy)
    {
        var graphics = Graphics;
        if (graphics is null || graphics.Count == 0) return;
        var stroke = Resource<double>("Vs.Size.GraphicStroke", 3);
        var fontSize = Resource<double>("Vs.Font.Body", 18);
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

        foreach (var g in graphics)
        {
            if (g.Points.Count == 0) continue;
            var name = g.Verdict switch
            {
                Verdict.Ok => "Ok",
                Verdict.Ng => "Ng",
                Verdict.Warn => "Warn",
                Verdict.Duplicate => "Duplicate",
                _ => "Info",
            };
            var brush = Resource<IBrush>("Vs.Brush.Status." + name, Brushes.Transparent);
            var onBrush = Resource<IBrush>("Vs.Brush.OnStatus." + name, Brushes.Transparent);
            var pen = new Pen(brush, stroke);
            var points = g.Points.Select(p => new Point(ox + p.X * scale, oy + p.Y * scale)).ToArray();

            if (g.Kind == GraphicKind.Cross)
            {
                var c = points[0];
                var arm = stroke * 4;
                context.DrawLine(pen, new Point(c.X - arm, c.Y), new Point(c.X + arm, c.Y));
                context.DrawLine(pen, new Point(c.X, c.Y - arm), new Point(c.X, c.Y + arm));
            }
            else if (g.Kind != GraphicKind.Text)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(points[0], false);
                    foreach (var p in points.Skip(1)) ctx.LineTo(p);
                    ctx.EndFigure(g.Kind == GraphicKind.Polygon);
                }
                context.DrawGeometry(null, pen, geometry);
            }

            if (!g.Tagged) continue;
            // Label: shape plus one word, on a tag in the status colour, above the box (a text graphic, a label
            // with no shape of its own, goes under the box its points give)
            var glyph = g.Verdict switch
            {
                Verdict.Ok => "✓",
                Verdict.Ng => "✗",
                Verdict.Duplicate => "⧉",
                Verdict.Warn => "!",
                _ => "i",
            };
            var word = g.Label ?? "";
            var label = new FormattedText($"{glyph} {word}".Trim(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, fontSize, onBrush);
            var top = points.MinBy(p => p.Y);
            var left = points.Min(p => p.X);
            var tag = new Rect(left, top.Y - label.Height - 6, label.Width + 12, label.Height + 4);
            if (tag.Y < 0 || g.Kind == GraphicKind.Text) tag = tag.WithY(points.Max(p => p.Y) + 2);
            context.DrawRectangle(brush, null, tag, 4, 4);
            context.DrawText(label, new Point(tag.X + 6, tag.Y + 2));
        }
    }

    private T Resource<T>(string key, T fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is T typed ? typed : fallback;
}
