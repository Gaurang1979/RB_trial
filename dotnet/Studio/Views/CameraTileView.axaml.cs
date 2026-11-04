using Avalonia.Controls;
using Avalonia.VisualTree;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Views;

public partial class CameraTileView : UserControl
{
    public CameraTileView()
    {
        InitializeComponent();
        // a tap on this camera's picture: the Run screen shows the tooltip of the graphic there
        this.FindControl<ResultOverlay>("Overlay")!.ImageTapped += (at, slop) =>
        {
            if (DataContext is CameraTile tile && this.FindAncestorOfType<RunView>()?.DataContext is RunViewModel run)
                run.TapImage(tile, at, slop);
        };
    }
}
