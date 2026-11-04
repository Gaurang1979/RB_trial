using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// The small overview in the corner of a zoomed picture (UI-UX-SPEC.md 4.19, requirements AS-17): the
/// whole picture, laid out as in the <see cref="ZoomBox"/> at fit, with the part on screen as a box.
/// Pressing or dragging moves the view so that point is in the middle.
/// </summary>
public sealed class ZoomOverview : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ZoomOverview, IImage?>(nameof(Source));

    /// <summary>The part on screen, 0-1 across and down (<see cref="ZoomBox.View"/>).</summary>
    public static readonly StyledProperty<Rect> ViewProperty =
        AvaloniaProperty.Register<ZoomOverview, Rect>(nameof(View), new Rect(0, 0, 1, 1));

    private bool _dragging;

    /// <summary>The engineer pointed at this point of the picture, 0-1 across and down.</summary>
    public event Action<Point>? Moved;

    static ZoomOverview()
    {
        AffectsRender<ZoomOverview>(SourceProperty, ViewProperty);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Rect View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        context.FillRectangle(Resource<IBrush>("Vs.Brush.Surface", Brushes.Black), new Rect(size));
        if (Source is { Size: { Width: > 0, Height: > 0 } s })
        {
            // Stretch="Uniform", as the picture in the zoom box
            var (scale, x, y) = ResultOverlay.Fit(size, s.Width, s.Height);
            context.DrawImage(Source, new Rect(s), new Rect(x, y, s.Width * scale, s.Height * scale));
        }
        var v = View;
        var box = new Rect(v.X * size.Width, v.Y * size.Height, v.Width * size.Width, v.Height * size.Height).Intersect(new Rect(size));
        context.DrawRectangle(null, new Pen(Resource<IBrush>("Vs.Brush.Status.Info", Brushes.DeepSkyBlue), 2), box);
        context.DrawRectangle(null, new Pen(Resource<IBrush>("Vs.Brush.Border", Brushes.Gray), 1), new Rect(size));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        MoveTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) MoveTo(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private void MoveTo(Point at)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        Moved?.Invoke(new Point(Math.Clamp(at.X / size.Width, 0, 1), Math.Clamp(at.Y / size.Height, 0, 1)));
    }

    private T Resource<T>(string key, T fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is T t ? t : fallback;
}
