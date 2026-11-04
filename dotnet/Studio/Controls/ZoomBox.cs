using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// Zoom and pan for the Engineer workspace's picture (UI-UX-SPEC.md 4.19, requirements AS-17). The child
/// (picture, result graphics, area drawing) is scaled and moved as one, so graphics stay on the picture and
/// an area drawn on a zoomed picture lands in the right image pixels. Mouse wheel and two fingers zoom about
/// the pointer; the right or middle button drags the picture, and so do the left button and one finger with
/// <see cref="PanTool"/> on (otherwise they stay the child's: drawing an area); the arrow keys move it.
/// Zoom 1 is the whole picture fitted in the box; zoom never goes below that.
/// </summary>
public sealed class ZoomBox : Decorator
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ZoomBox, double>(nameof(Zoom), 1, coerce: (_, v) => Math.Clamp(double.IsFinite(v) ? v : 1, MinZoom, MaxZoom));

    /// <summary>Left button and one finger pan instead of drawing an area.</summary>
    public static readonly StyledProperty<bool> PanToolProperty =
        AvaloniaProperty.Register<ZoomBox, bool>(nameof(PanTool));

    public const double MinZoom = 1, MaxZoom = 16, Step = 1.25;
    /// <summary>How far one arrow key press moves the picture, as a part of the box.</summary>
    public const double KeyPan = 0.1;

    private Vector _offset;
    private Point? _dragFrom;
    private double _pinchScale = 1;

    /// <summary>The zoom or the part of the picture on screen changed (scroll bars, overview).</summary>
    public event EventHandler? ViewChanged;

    static ZoomBox()
    {
        ClipToBoundsProperty.OverrideDefaultValue<ZoomBox>(true);
        BackgroundProperty.OverrideDefaultValue<ZoomBox>(Brushes.Transparent);
        FocusableProperty.OverrideDefaultValue<ZoomBox>(true);
    }

    public ZoomBox()
    {
        // before the child sees it: the pan tool takes the left button, and a click gives the arrow keys to the picture
        AddHandler(PointerPressedEvent, OnPressTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(Gestures.PinchEvent, OnPinch);
        AddHandler(Gestures.PinchEndedEvent, (_, _) => _pinchScale = 1);
        AddHandler(Gestures.PointerTouchPadGestureMagnifyEvent, OnMagnify);
    }

    /// <summary>How much larger than "fit" the picture is shown: 1 = fit, 2 = twice, up to 16.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool PanTool
    {
        get => GetValue(PanToolProperty);
        set => SetValue(PanToolProperty, value);
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ZoomBox>();

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Zoom in or out by one step about a point of the box (the centre when null).</summary>
    public void ZoomBy(double factor, Point? about = null)
    {
        var at = about ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var old = Zoom;
        var before = _offset;   // setting Zoom clamps the offset to the new limits first
        Zoom = old * factor;
        var scale = Zoom / old;
        // keep the point under the pointer where it is
        _offset = new Vector(at.X - (at.X - before.X) * scale, at.Y - (at.Y - before.Y) * scale);
        Apply();
    }

    /// <summary>Zoom to a level (1 = fit) about a point of the box (the centre when null).</summary>
    public void ZoomTo(double zoom, Point? about = null) => ZoomBy(zoom / Zoom, about);

    /// <summary>Back to the whole picture.</summary>
    public void Fit()
    {
        Zoom = 1;
        _offset = default;
        Apply();
    }

    /// <summary>100 %: one camera pixel per screen pixel, about the centre of the box.</summary>
    public void OneToOne(double pictureWidth, double pictureHeight) =>
        ZoomTo(OneToOneZoom(Bounds.Size, pictureWidth, pictureHeight, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));

    /// <summary>The zoom at which one camera pixel is one screen (device) pixel, for a picture of this many
    /// camera pixels fitted in the box; within the zoom limits (a picture smaller than the box stays at fit).</summary>
    public static double OneToOneZoom(Size box, double pictureWidth, double pictureHeight, double renderScaling)
    {
        if (box.Width <= 0 || box.Height <= 0 || pictureWidth <= 0 || pictureHeight <= 0 || renderScaling <= 0) return MinZoom;
        var fit = Math.Min(box.Width / pictureWidth, box.Height / pictureHeight);   // box units per camera pixel at fit
        return Math.Clamp(1 / (fit * renderScaling), MinZoom, MaxZoom);
    }

    /// <summary>Move the picture by this many box pixels (within the limits).</summary>
    public void PanBy(Vector by)
    {
        _offset += by;
        Apply();
    }

    /// <summary>Show the picture with its top-left corner at this offset (zero or less; within the limits).</summary>
    public void PanTo(Vector offset)
    {
        _offset = offset;
        Apply();
    }

    /// <summary>Put this point of the picture (0-1 across and down the box at fit) in the middle of the box.</summary>
    public void CentreOn(Point at)
    {
        var w = Bounds.Width * Zoom;
        var h = Bounds.Height * Zoom;
        PanTo(new Vector(Bounds.Width / 2 - at.X * w, Bounds.Height / 2 - at.Y * h));
    }

    /// <summary>The part of the picture on screen, 0-1 across and down the box at fit (the whole box at fit).</summary>
    public Rect View => ViewOf(Bounds.Size, Zoom, _offset);

    public static Rect ViewOf(Size box, double zoom, Vector offset) =>
        box.Width <= 0 || box.Height <= 0 ? new Rect(0, 0, 1, 1)
            : new Rect(-offset.X / (box.Width * zoom), -offset.Y / (box.Height * zoom), 1 / zoom, 1 / zoom);

    /// <summary>The offset kept within the picture: no empty side larger than the gap at fit.</summary>
    public static Vector Clamp(Vector offset, Size box, double zoom) =>
        zoom <= MinZoom ? default
            : new Vector(Math.Clamp(offset.X, box.Width - box.Width * zoom, 0), Math.Clamp(offset.Y, box.Height - box.Height * zoom, 0));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty || change.Property == BoundsProperty) Apply();
    }

    public override void Render(DrawingContext context)
    {
        if (Background is { } b) context.FillRectangle(b, new Rect(Bounds.Size));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        ZoomBy(e.Delta.Y > 0 ? Step : 1 / Step, e.GetPosition(this));
        e.Handled = true;
    }

    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        if (!PanTool || !props.IsLeftButtonPressed) return;
        StartDrag(e);   // the child never sees it: no area is drawn with the pan tool
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsRightButtonPressed && !props.IsMiddleButtonPressed) return;
        StartDrag(e);
    }

    private void StartDrag(PointerPressedEventArgs e)
    {
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsRightButtonPressed && !props.IsMiddleButtonPressed && !(PanTool && props.IsLeftButtonPressed)) { _dragFrom = null; return; }
        var at = e.GetPosition(this);
        _dragFrom = at;
        PanBy(at - from);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;   // taken away mid-drag (another window, a dialog): no pan left running
    }

    /// <summary>Two fingers on a touch screen: the scale is from the start of the pinch.</summary>
    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (e.Scale <= 0) return;
        ZoomBy(e.Scale / _pinchScale, e.ScaleOrigin);
        _pinchScale = e.Scale;
        e.Handled = true;
    }

    /// <summary>Two fingers on a touch pad.</summary>
    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (1 + e.Delta.X <= 0) return;
        ZoomBy(1 + e.Delta.X, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        Vector? by = e.Key switch
        {
            Key.Left => new Vector(Bounds.Width * KeyPan, 0),
            Key.Right => new Vector(-Bounds.Width * KeyPan, 0),
            Key.Up => new Vector(0, Bounds.Height * KeyPan),
            Key.Down => new Vector(0, -Bounds.Height * KeyPan),
            _ => null,
        };
        if (by is null || Zoom <= MinZoom) return;
        PanBy(by.Value);   // the arrow shows more of the picture on its side
        e.Handled = true;
    }

    /// <summary>Scale and move the child; the picture never leaves the box (no empty side larger than the gap at fit).</summary>
    private void Apply()
    {
        _offset = Clamp(_offset, Bounds.Size, Zoom);
        if (Child is null) return;
        var z = Zoom;
        Child.RenderTransformOrigin = RelativePoint.TopLeft;
        Child.RenderTransform = new MatrixTransform(Matrix.CreateScale(z, z) * Matrix.CreateTranslation(_offset));
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The offset of the scaled child, in box pixels (for tests).</summary>
    public Vector Offset => _offset;
}
