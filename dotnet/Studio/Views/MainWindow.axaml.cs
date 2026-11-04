using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VisionSystem.Studio.ViewModels;

namespace VisionSystem.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tunnel, so the window sees every key before a text box does: the card reader's Enter
        // must not also press OK in the password box.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnText, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => (DataContext as MainViewModel)?.NoteInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.NoteInput();
        switch (e.Key)
        {
            case Key.Enter:
                var card = vm.Card.Enter(out var burst);
                if (burst) e.Handled = true;
                if (card is not null) _ = vm.CardReadAsync(card);
                break;
            case Key.Back or Key.Delete or Key.Tab or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down:
                vm.Card.Clear();
                break;
        }
    }

    private void OnText(object? sender, TextInputEventArgs e) => (DataContext as MainViewModel)?.Card.Text(e.Text);
}
