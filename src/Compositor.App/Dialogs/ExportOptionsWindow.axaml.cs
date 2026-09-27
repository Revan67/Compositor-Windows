using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Compositor.App.Dialogs;

public readonly record struct ExportOptions(int JpegQuality, int PngCompression);

public sealed partial class ExportOptionsWindow : Window
{
    public ExportOptionsWindow() => InitializeComponent();

    public static Task<ExportOptions?> ShowAsync(Window owner, bool jpeg)
    {
        var dialog = new ExportOptionsWindow();
        dialog.Title = jpeg ? "JPEG Export Options" : "PNG Export Options";
        dialog.Heading.Text = jpeg ? "JPEG compression" : "PNG compression";
        dialog.JpegPanel.IsVisible = jpeg;
        dialog.PngPanel.IsVisible = !jpeg;
        return dialog.ShowDialog<ExportOptions?>(owner);
    }

    private void Accept(object? sender, RoutedEventArgs e) => Close(new ExportOptions(
        Math.Clamp((int)(QualityBox.Value ?? 85), 1, 100),
        Math.Clamp((int)(CompressionBox.Value ?? 6), 0, 9)));

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
