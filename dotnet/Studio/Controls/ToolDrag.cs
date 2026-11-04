using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VisionSystem.Studio.Runtime;

namespace VisionSystem.Studio.Controls;

/// <summary>
/// Drags a toolbox tile onto the picture (UI-UX-SPEC.md 5 "Drop on image"): press on the tile and move a
/// few pixels, and the tool's starting area follows the pointer over the picture, dashed; release over the
/// picture drops the tool there. A press released on the tile without moving stays the tile's click
/// (tap-to-add, also for the keyboard). The tile keeps the pointer while it is pressed, so the drag is
/// followed on the tile itself: no system drag and drop, and a finger works as the mouse does.
/// </summary>
public static class ToolDrag
{
    /// <summary>How far the pointer moves before a press on a tile becomes a drag, in screen pixels.</summary>
    public const double Start = 6;

    /// <summary>Follows drags from a tile onto the picture's <see cref="AreaDrag"/>. Both callbacks take image
    /// pixels: <paramref name="preview"/> gives the area to show under the pointer (null for none), and
    /// <paramref name="dropped"/> is called once for a release over the picture.</summary>
    public static void Attach(Control tile, AreaDrag picture, Func<Point2, (Point2, Point2)?> preview, Action<Point2> dropped)
    {
        Point? from = null;
        var dragging = false;
        void End()
        {
            from = null;
            dragging = false;
            picture.ShowDrop(null);
            tile.Cursor = null;
        }
        // tunnel, and handled ones too: the tile (a button) takes the press and release for its click
        tile.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            (from, dragging) = (e.GetPosition(tile), false);
            e.Pointer.Capture(tile);   // the tile itself, not the label pressed on: it hears a lost capture
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (from is not { } f) return;
            var p = e.GetPosition(tile);
            if (!dragging && Math.Abs(p.X - f.X) + Math.Abs(p.Y - f.Y) < Start) return;
            dragging = true;
            tile.Cursor = new Cursor(StandardCursorType.DragCopy);
            picture.ShowDrop(Over(picture, e) is { } at ? preview(at) : null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            var at = dragging ? Over(picture, e) : null;
            End();
            // after the tile's own release (the drop makes the toolbox busy, which disables the tile)
            if (at is { } p) Avalonia.Threading.Dispatcher.UIThread.Post(() => dropped(p));
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        tile.PointerCaptureLost += (_, _) => End();   // taken away mid-drag (another window, a dialog): nothing dropped
    }

    /// <summary>The pointer in image pixels when it is over the picture as shown (inside the zoom's window and on
    /// the picture itself); null elsewhere.</summary>
    public static Point2? Over(AreaDrag picture, PointerEventArgs e)
    {
        if (!picture.IsEffectivelyVisible) return null;
        var window = picture.FindAncestorOfType<ZoomBox>() as Visual ?? picture;
        var seen = e.GetPosition(window);
        if (!new Rect(window.Bounds.Size).Contains(seen)) return null;
        return picture.ToImage(e.GetPosition(picture)) is { } at && at.X >= 0 && at.Y >= 0 && at.X < picture.ImageWidth && at.Y < picture.ImageHeight
            ? at : null;
    }
}
