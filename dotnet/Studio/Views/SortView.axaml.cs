using Avalonia.Controls;
using Avalonia.Input;
using VisionSystem.Studio.Controls;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Views;

public partial class SortView : UserControl
{
    public SortView()
    {
        InitializeComponent();
        var zoom = this.FindControl<ZoomBox>("SortZoom")!;
        this.FindControl<Button>("SortZoomIn")!.Click += (_, _) => zoom.ZoomBy(ZoomBox.Step);
        this.FindControl<Button>("SortZoomOut")!.Click += (_, _) => zoom.ZoomBy(1 / ZoomBox.Step);
        this.FindControl<Button>("SortZoomFit")!.Click += (_, _) => zoom.Fit();
        // a new picture starts fitted
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SortViewModel vm)
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SortViewModel.Current)) zoom.Fit(); };
        };
        AttachedToVisualTree += (_, _) => Focus();
    }

    /// <summary>A, R, ← and → for a keyboard at the line PC; not while typing in a field.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not SortViewModel vm || e.Source is TextBox) return;
        var command = e.Key switch
        {
            Key.A => vm.DoubleCheck ? null : vm.AcceptCommand,
            Key.R => vm.RejectCommand,
            Key.Left => vm.PreviousCommand,
            Key.Right => vm.NextCommand,
            _ => (System.Windows.Input.ICommand?)null,
        };
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }
}
