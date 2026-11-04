using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using VisionSystem.Studio.Controls;
using Avalonia.VisualTree;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Views;

public partial class DesignerView : UserControl
{
    private DesignerViewModel? _vm;
    /// <summary>Each panel's size before it was hidden, so it comes back as the engineer left it.</summary>
    private double _jobWidth = 232, _propertiesWidth = 344, _docksHeight = 200;
    private bool _jobShown = true, _propertiesShown = true, _docksShown = true;

    public DesignerView()
    {
        InitializeComponent();
        this.FindControl<AreaDrag>("AreaDrag")!.AreaDrawn += (from, to) => (DataContext as DesignerViewModel)?.AreaDrawn(from, to);
        // Page Up / Page Down change the picture even when a list or the property table has the focus
        // (a scroll viewer would take them first); in a text box or a list box they keep their own meaning.
        AddHandler(KeyDownEvent, OnPageKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnDeleteKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnUndoKey, RoutingStrategies.Tunnel);
        WireToolbox();

        var zoom = this.FindControl<ZoomBox>("Zoom")!;
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => zoom.ZoomBy(ZoomBox.Step);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => zoom.ZoomBy(1 / ZoomBox.Step);
        this.FindControl<Button>("ZoomFitButton")!.Click += (_, _) => zoom.Fit();
        this.FindControl<Button>("Zoom100Button")!.Click += (_, _) => OneToOne();
        // the slider is the zoom's power of two: 0 = fit, 1 = twice, 4 = 16 times
        this.FindControl<Slider>("ZoomSlider")!.ValueChanged += (_, e) =>
        {
            if (!_syncing) zoom.ZoomTo(Math.Pow(2, e.NewValue));
        };
        zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property != ZoomBox.ZoomProperty) return;
            ShowZoom();
        };
        zoom.ViewChanged += (_, _) =>
        {
            SyncView();
            ShowPixels();   // also when the box changes size
        };
        this.FindControl<ScrollBar>("ZoomScrollX")!.ValueChanged += (_, e) =>
        {
            if (!_syncing) zoom.PanTo(new Avalonia.Vector(-e.NewValue, zoom.Offset.Y));
        };
        this.FindControl<ScrollBar>("ZoomScrollY")!.ValueChanged += (_, e) =>
        {
            if (!_syncing) zoom.PanTo(new Avalonia.Vector(zoom.Offset.X, -e.NewValue));
        };
        this.FindControl<ZoomOverview>("Overview")!.Moved += zoom.CentreOn;
        zoom.PointerMoved += (_, e) => ReadPixel(e);
        zoom.PointerExited += (_, _) => _vm?.PointAt(null, null, null);
        // a tap on an area selects it, and its row in "Areas and values"
        this.FindControl<AreaDrag>("AreaDrag")!.AreaDrawn += (from, to) => _vm?.PictureTapped(from, to);
        // Ctrl+0 the whole picture, Ctrl+1 100 %, wherever the focus is in the workspace
        AddHandler(KeyDownEvent, OnZoomKey, RoutingStrategies.Tunnel);
    }

    /// <summary>The scroll bars, slider and overview are being set from the zoom: their changes are not the engineer's.</summary>
    private bool _syncing;

    /// <summary>The zoom label in camera pixels: screen pixels per camera pixel at this zoom.</summary>
    private void ShowZoom()
    {
        if (_vm is null || this.FindControl<ZoomBox>("Zoom") is not { } zoom) return;
        var size = zoom.Bounds.Size;
        var perCamera = _vm.CameraWidth > 0 && size.Width > 0 && size.Height > 0
            ? zoom.Zoom * Math.Min(size.Width / _vm.CameraWidth, size.Height / _vm.CameraHeight) * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)
            : 0;
        _vm.ShowZoom(zoom.Zoom, perCamera);
    }

    private void OneToOne()
    {
        if (_vm is { CameraWidth: > 0 } vm) this.FindControl<ZoomBox>("Zoom")!.OneToOne(vm.CameraWidth, vm.CameraHeight);
    }

    private void OnZoomKey(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & ~KeyModifiers.Shift) is not (KeyModifiers.Control or KeyModifiers.Meta)) return;
        var zoom = this.FindControl<ZoomBox>("Zoom")!;
        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0: zoom.Fit(); break;
            case Key.D1 or Key.NumPad1: OneToOne(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Scroll bars, slider and overview follow the zoom; scroll bars and overview only when zoomed in.</summary>
    private void SyncView()
    {
        var zoom = this.FindControl<ZoomBox>("Zoom")!;
        var size = zoom.Bounds.Size;
        var zoomed = zoom.Zoom > ZoomBox.MinZoom;
        _syncing = true;
        try
        {
            this.FindControl<Slider>("ZoomSlider")!.Value = Math.Log2(zoom.Zoom);
            Bar("ZoomScrollX", size.Width, -zoom.Offset.X);
            Bar("ZoomScrollY", size.Height, -zoom.Offset.Y);
            var overview = this.FindControl<ZoomOverview>("Overview")!;
            overview.IsVisible = zoomed && _vm?.OverviewImage is not null;
            overview.View = zoom.View;
            // laid out as the box: the same part of the picture at the same place
            if (size.Width > 0 && size.Height > 0) overview.Height = Math.Round(overview.Width * size.Height / size.Width);
        }
        finally { _syncing = false; }

        void Bar(string name, double length, double value)
        {
            var bar = this.FindControl<ScrollBar>(name)!;
            bar.IsVisible = zoomed;
            bar.Minimum = 0;
            bar.Maximum = Math.Max(0, length * zoom.Zoom - length);
            bar.ViewportSize = length;
            bar.SmallChange = length * ZoomBox.KeyPan;
            bar.LargeChange = length * 0.9;
            bar.Value = value;
        }
    }

    /// <summary>Each pixel of the picture shown is drawn as a square, as it is, once it covers a screen pixel or more;
    /// smaller, the picture is smoothed.</summary>
    private void ShowPixels()
    {
        if (this.FindControl<Image>("DesignImage") is not { } image || this.FindControl<ZoomBox>("Zoom") is not { } zoom) return;
        var perPixel = 0.0;
        if (image.Source is { Size: { Width: > 0, Height: > 0 } s })
            perPixel = zoom.Zoom * Math.Min(image.Bounds.Width / s.Width, image.Bounds.Height / s.Height) * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(image, perPixel >= 0.99   // layout rounding at 100 %
            ? Avalonia.Media.Imaging.BitmapInterpolationMode.None : Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    /// <summary>The picture's pixel under the pointer (the image is fitted with Uniform, then zoomed) and its grey value.</summary>
    private void ReadPixel(PointerEventArgs e)
    {
        if (_vm is null || this.FindControl<Image>("DesignImage") is not { Source: { } source } image) return;
        var size = source.Size;
        if (size.Width <= 0 || size.Height <= 0 || image.Bounds.Width <= 0 || image.Bounds.Height <= 0) return;
        var fit = Math.Min(image.Bounds.Width / size.Width, image.Bounds.Height / size.Height);
        var left = (image.Bounds.Width - size.Width * fit) / 2;
        var top = (image.Bounds.Height - size.Height * fit) / 2;
        var at = e.GetPosition(image);   // through the zoom's transform
        var x = (int)Math.Floor((at.X - left) / fit);
        var y = (int)Math.Floor((at.Y - top) / fit);
        if (x < 0 || y < 0 || x >= size.Width || y >= size.Height) { _vm.PointAt(null, null, null); return; }
        _vm.PointAt(x, y, source is Avalonia.Media.Imaging.Bitmap bitmap ? Grey(bitmap, x, y) : null);
    }

    /// <summary>The grey value (0-255) of one pixel, or null when the bitmap cannot be read.</summary>
    private static int? Grey(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var format = bitmap.Format;
        var gray = format == Avalonia.Platform.PixelFormats.Gray8;
        if (!gray && format != Avalonia.Platform.PixelFormat.Bgra8888 && format != Avalonia.Platform.PixelFormat.Rgba8888)
            return null;   // a format this does not read: position only
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 0);
            bitmap.CopyPixels(new Avalonia.PixelRect(x, y, 1, 1), buffer, 4, gray ? 1 : 4);
            if (gray) return System.Runtime.InteropServices.Marshal.ReadByte(buffer, 0);
            var b = System.Runtime.InteropServices.Marshal.ReadByte(buffer, 0);
            var g = System.Runtime.InteropServices.Marshal.ReadByte(buffer, 1);
            var r = System.Runtime.InteropServices.Marshal.ReadByte(buffer, 2);
            if (format == Avalonia.Platform.PixelFormat.Rgba8888) (r, b) = (b, r);
            return (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
        }
        catch (Exception) { return null; }   // a format this cannot read: position only
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The property search and the Essential / All switch: rows whose label does not match, or
    /// advanced rows with Essential, are left out; a header with nothing left under it goes too.</summary>
    private void FilterProperties()
    {
        if (_vm is null || this.FindControl<ScrollViewer>("PropertiesScroll")?.Content is not Panel root) return;
        var searching = _vm.PropertySearch.Trim().Length > 0;
        var shown = Filter(root, searching);
        if (this.FindControl<TextBlock>("NoSetting") is { } none) none.IsVisible = shown == 0;
    }

    /// <summary>Filters one panel's rows; returns how many rows it leaves showing.</summary>
    private int Filter(Panel panel, bool searching)
    {
        var shown = 0;
        Border? header = null;
        var underHeader = 0;
        void CloseHeader()
        {
            header?.Classes.Set("filtered", underHeader == 0 && (searching || !_vm!.ShowAllProperties));
            header = null;
            underHeader = 0;
        }
        foreach (var child in panel.Children)
        {
            switch (child)
            {
                case Border b when b.Classes.Contains("pcat"):
                    CloseHeader();
                    header = b;
                    break;
                case Grid g when g.Classes.Contains("prow"):
                    var label = string.Join(" ", g.Children.OfType<TextBlock>().Select(t => t.Text)
                        .Concat(g.Children.OfType<CheckBox>().Select(c => c.Content as string)));
                    var keep = _vm!.PropertyShown(label, !g.Classes.Contains("adv"));
                    g.Classes.Set("filtered", !keep);
                    if (keep) { shown++; underHeader++; }
                    break;
                case TextBlock t when t.Classes.Contains("hint"):
                    t.Classes.Set("filtered", searching);
                    break;
                case Panel p:
                    var n = Filter(p, searching);
                    if (!p.IsVisible) break;   // hidden for this tool: filtered all the same, counted as nothing
                    shown += n;
                    // a panel with its own headers keeps its rows to itself; one without counts under ours
                    if (!p.Children.OfType<Border>().Any(c => c.Classes.Contains("pcat"))) underHeader += n;
                    break;
                case ItemsControl or Button:
                    // the expected text lines and "+ Line" go with their header's words ("Expected lines")
                    var words = (header?.Child as TextBlock)?.Text ?? "";
                    var show = !searching || _vm!.PropertyShown(words, essential: true);
                    child.Classes.Set("filtered", !show);
                    if (show && child is ItemsControl) { shown++; underHeader++; }
                    break;
            }
        }
        CloseHeader();
        return shown;
    }

    /// <summary>The toolbox tiles drag onto the picture (UI-UX-SPEC.md 5); a click still adds the tool.</summary>
    private void WireToolbox()
    {
        var picture = this.FindControl<AreaDrag>("AreaDrag")!;
        foreach (var (tile, kind) in new[] { ("AddReadCode", ToolKind.ReadCode), ("AddReadText", ToolKind.ReadText), ("AddCheckText", ToolKind.CheckText), ("AddBlob", ToolKind.Blob) })
            ToolDrag.Attach(this.FindControl<Button>(tile)!, picture, at => _vm?.DropPreview(kind, at), at => _ = _vm?.DropToolAsync(kind, at));
    }

    private void OnSnapped(Runtime.Point2 from, Runtime.Point2 to) => this.FindControl<AreaDrag>("AreaDrag")?.Flash(from, to);

    /// <summary>Delete removes the selected tool (Undo puts it back); in a text box it keeps its own meaning.</summary>
    private void OnDeleteKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None || _vm is not { Busy: false, HasTool: true }) return;
        if (e.Source is TextBox or ComboBox or NumericUpDown || (e.Source as Control)?.FindAncestorOfType<TextBox>() is not null) return;
        _vm.RemoveToolCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Ctrl+Z undoes the last change of the draft, Ctrl+Y and Ctrl+Shift+Z redo it (AS-18); in a text box
    /// they stay the text box's own.</summary>
    private void OnUndoKey(object? sender, KeyEventArgs e)
    {
        var action = (e.Key, e.KeyModifiers) switch
        {
            (Key.Z, KeyModifiers.Control) => "undo",
            (Key.Y, KeyModifiers.Control) or (Key.Z, KeyModifiers.Control | KeyModifiers.Shift) => "redo",
            _ => null,
        };
        if (action is null || _vm is null) return;
        if (e.Source is TextBox || (e.Source as Control)?.FindAncestorOfType<TextBox>() is not null) return;
        _vm.Shortcut(action);
        e.Handled = true;
    }

    private void OnPageKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown) || e.KeyModifiers != KeyModifiers.None || _vm is null) return;
        if (e.Source is TextBox or ComboBox or NumericUpDown || (e.Source as Control)?.FindAncestorOfType<TextBox>() is not null) return;
        _vm.Shortcut(e.Key == Key.PageUp ? "prev" : "next");
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.AreaSnapped -= OnSnapped; }
        _vm = DataContext as DesignerViewModel;
        if (_vm is not null) { _vm.PropertyChanged += OnVmChanged; _vm.AreaSnapped += OnSnapped; }
        Arrange();
        FilterProperties();
        ShowZoom();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignerViewModel.ShowJobPanel) or nameof(DesignerViewModel.ShowPropertiesPanel) or nameof(DesignerViewModel.ShowDocks))
            Arrange();
        if (e.PropertyName is nameof(DesignerViewModel.PropertySearch) or nameof(DesignerViewModel.ShowAllProperties)
            or nameof(DesignerViewModel.SelectedTool) or nameof(DesignerViewModel.Camera) or nameof(DesignerViewModel.HasTool))
            // after the tool's own panels have shown or hidden (their bindings update in the same pass)
            Avalonia.Threading.Dispatcher.UIThread.Post(FilterProperties, Avalonia.Threading.DispatcherPriority.Loaded);
        if (e.PropertyName is nameof(DesignerViewModel.Image))
        {
            _vm?.PointAt(null, null, null);
            ShowPixels();   // the full picture has more pixels than the reduced one
        }
        if (e.PropertyName is nameof(DesignerViewModel.OverviewImage))
        {
            SyncView();
            ShowZoom();   // another camera's picture: another pixel count
        }
    }

    /// <summary>A hidden panel gives its column or row to the picture; shown again, it comes back at the size it had.
    /// Only the panel shown or hidden changes, so the others keep a size the engineer dragged.</summary>
    private void Arrange()
    {
        if (_vm is null || this.FindControl<Grid>("Docks") is not { } grid) return;
        var cols = grid.ColumnDefinitions;
        var rows = grid.RowDefinitions;
        if (_vm.ShowJobPanel != _jobShown)
        {
            if (!_vm.ShowJobPanel) _jobWidth = Math.Max(120, cols[0].ActualWidth);
            cols[0].Width = new GridLength(_vm.ShowJobPanel ? _jobWidth : 0);
            cols[1].Width = new GridLength(_vm.ShowJobPanel ? 4 : 0);
            _jobShown = _vm.ShowJobPanel;
        }
        if (_vm.ShowPropertiesPanel != _propertiesShown)
        {
            if (!_vm.ShowPropertiesPanel) _propertiesWidth = Math.Max(200, cols[4].ActualWidth);
            cols[3].Width = new GridLength(_vm.ShowPropertiesPanel ? 4 : 0);
            cols[4].Width = new GridLength(_vm.ShowPropertiesPanel ? _propertiesWidth : 0);
            _propertiesShown = _vm.ShowPropertiesPanel;
        }
        if (_vm.ShowDocks != _docksShown)
        {
            if (!_vm.ShowDocks) _docksHeight = Math.Max(120, rows[2].ActualHeight);
            rows[1].Height = new GridLength(_vm.ShowDocks ? 4 : 0);
            rows[2].Height = new GridLength(_vm.ShowDocks ? _docksHeight : 0);
            _docksShown = _vm.ShowDocks;
        }
    }
}
