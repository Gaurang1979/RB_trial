using Avalonia.Controls;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Views;

public partial class NgViewerView : UserControl
{
    public NgViewerView()
    {
        InitializeComponent();
        this.FindControl<ResultOverlay>("NgOverlay")!.ImageTapped += (at, slop) => (DataContext as NgViewerViewModel)?.TapImage(at, slop);
        var zoom = this.FindControl<ZoomBox>("NgZoom")!;
        this.FindControl<Button>("NgZoomIn")!.Click += (_, _) => zoom.ZoomBy(ZoomBox.Step);
        this.FindControl<Button>("NgZoomOut")!.Click += (_, _) => zoom.ZoomBy(1 / ZoomBox.Step);
        this.FindControl<Button>("NgZoomFit")!.Click += (_, _) => zoom.Fit();
    }
}
