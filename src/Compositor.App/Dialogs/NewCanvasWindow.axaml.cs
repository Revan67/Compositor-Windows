using Avalonia.Controls;
using Avalonia.Interactivity;
using Compositor.Core.Document;

namespace Compositor.App.Dialogs;

public sealed partial class NewCanvasWindow : Window
{
    public NewCanvasWindow() => InitializeComponent();

    public static Task<(int Width, int Height)?> ShowAsync(Window owner) => new NewCanvasWindow().ShowDialog<(int, int)?>(owner);

    private void Create(object? sender, RoutedEventArgs e)
    {
        var width = CanvasDocument.ValidDimension(WidthBox.Value?.ToString("0") ?? string.Empty);
        var height = CanvasDocument.ValidDimension(HeightBox.Value?.ToString("0") ?? string.Empty);
        if (width is { } w && height is { } h)
        {
            Close((w, h));
        }
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
