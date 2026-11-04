using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// Draws a tool's area with a finger or the mouse over a picture shown with Stretch="Uniform" in
/// the same cell (the job designer's canvas): press, drag, release. The rubber band follows the
/// finger; on release <see cref="AreaDrawn"/> gives both corners in image pixels (the picture's
/// own, as <see cref="ResultOverlay"/> maps them). It also shows where a tool dragged from the toolbox
/// would land (<see cref="ShowDrop"/>) and flashes an area that snapped (<see cref="Flash"/>).
/// </summary>
public sealed class AreaDrag : Control
{
    public static readonly StyledProperty<double> ImageWidthProperty =
        AvaloniaProperty.Register<AreaDrag, double>(nameof(ImageWidth), 1);

    public static readonly StyledProperty<double> ImageHeightProperty =
        AvaloniaProperty.Register<AreaDrag, double>(nameof(ImageHeight), 1);

    /// <summary>How long a snapped area stays lit.</summary>
    public static readonly TimeSpan FlashTime = TimeSpan.FromMilliseconds(600);

    private Point? _from, _to;
    /// <summary>The dragged tool's starting area and a snapped area, in image pixels.</summary>
    private (Point2 From, Point2 To)? _drop, _flash;
    private Avalonia.Threading.DispatcherTimer? _flashTimer;

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

    public event Action<Point2, Point2>? AreaDrawn;

    public AreaDrag()
    {
        Background = Brushes.Transparent;
        PointerPressed += (_, e) =>
        {
            // the left button (or a finger) draws; right and middle belong to the zoom's pan
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _from = _to = e.GetPosition(this);
            e.Pointer.Capture(this);
            InvalidateVisual();
        };
        PointerMoved += (_, e) =>
        {
            if (_from is null) return;
            _to = e.GetPosition(this);
            InvalidateVisual();
        };
        PointerReleased += (_, e) =>
        {
            if (_from is { } a && _to is { } b)
            {
                var (scale, ox, oy) = ResultOverlay.Fit(Bounds.Size, ImageWidth, ImageHeight);
                if (scale > 0) AreaDrawn?.Invoke(new Point2((a.X - ox) / scale, (a.Y - oy) / scale), new Point2((b.X - ox) / scale, (b.Y - oy) / scale));
            }
            _from = _to = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        };
        PointerCaptureLost += (_, _) =>
        {
            _from = _to = null;   // the pointer was taken away (another window, a dialog): no area half drawn
            InvalidateVisual();
        };
    }

    public IBrush? Background { get; set; }

    /// <summary>A point of this control in image pixels; null before the picture has a size.</summary>
    public Point2? ToImage(Point p)
    {
        var (scale, ox, oy) = ResultOverlay.Fit(Bounds.Size, ImageWidth, ImageHeight);
        return scale > 0 ? new Point2((p.X - ox) / scale, (p.Y - oy) / scale) : null;
    }

    /// <summary>The area a tool dragged from the toolbox would get (dashed), or none.</summary>
    public void ShowDrop((Point2 From, Point2 To)? area)
    {
        _drop = area;
        InvalidateVisual();
    }

    /// <summary>An area that snapped onto what was found, lit once.</summary>
    public void Flash(Point2 from, Point2 to)
    {
        _flash = (from, to);
        Flashes++;
        _flashTimer?.Stop();
        _flashTimer = new Avalonia.Threading.DispatcherTimer { Interval = FlashTime };
        _flashTimer.Tick += (_, _) => { _flashTimer?.Stop(); _flash = null; InvalidateVisual(); };
        _flashTimer.Start();
        InvalidateVisual();
    }

    /// <summary>Whether a dragged tool's area is shown now (for tests).</summary>
    public bool ShowsDrop => _drop is not null;

    /// <summary>How many snapped areas were lit (for tests: the light itself goes out after <see cref="FlashTime"/>).</summary>
    public int Flashes { get; private set; }

    public override void Render(DrawingContext context)
    {
        // transparent everywhere, so the whole picture takes the finger
        context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));
        var brush = this.TryFindResource("Vs.Brush.Status.Info", ActualThemeVariant, out var v) && v is IBrush ib ? ib : Brushes.DodgerBlue;
        var (scale, ox, oy) = ResultOverlay.Fit(Bounds.Size, ImageWidth, ImageHeight);
        Rect Shown((Point2 From, Point2 To) r) => new(new Point(ox + r.From.X * scale, oy + r.From.Y * scale), new Point(ox + r.To.X * scale, oy + r.To.Y * scale));
        if (_flash is { } f && scale > 0)
        {
            var ok = this.TryFindResource("Vs.Brush.Status.Ok", ActualThemeVariant, out var o) && o is IBrush ob ? ob : Brushes.SeaGreen;
            context.DrawRectangle(new SolidColorBrush((ok as ISolidColorBrush)?.Color ?? Colors.SeaGreen, 0.3), new Pen(ok, 3), Shown(f));
        }
        if (_drop is { } d && scale > 0) context.DrawRectangle(null, new Pen(brush, 2, new DashStyle([4, 3], 0)), Shown(d));
        if (_from is not { } a || _to is not { } b) return;
        context.DrawRectangle(null, new Pen(brush, 2, new DashStyle([4, 3], 0)), new Rect(a, b));
    }
}
