using Avalonia.Controls;
using Avalonia.Interactivity;
using Compositor.Core.Document;
using Compositor.Core.Project;
using SkiaSharp;

namespace Compositor.App.Dialogs;

public sealed partial class CanvasSizeWindow : Window
{
    public CanvasSizeWindow() => InitializeComponent();

    public static Task<CanvasSizeOptions?> ShowAsync(Window owner, CanvasDocument document)
    {
        var dialog = new CanvasSizeWindow();
        dialog.WidthBox.Value = document.Width;
        dialog.HeightBox.Value = document.Height;
        return dialog.ShowDialog<CanvasSizeOptions?>(owner);
    }

    private void Apply(object? sender, RoutedEventArgs e)
    {
        var width = CanvasDocument.ValidDimension(WidthBox.Value?.ToString("0") ?? string.Empty);
        var height = CanvasDocument.ValidDimension(HeightBox.Value?.ToString("0") ?? string.Empty);
        if (width is null || height is null)
        {
            ErrorText.Text = "Enter dimensions from 1 to 30,000 pixels.";
            return;
        }

        CanvasExtensionColor? fill = null;
        if (FillBox.IsChecked == true)
        {
            if (!SKColor.TryParse(FillColorBox.Text, out var color))
            {
                ErrorText.Text = "Enter a fill color such as #FFFFFF.";
                return;
            }

            fill = new CanvasExtensionColor(color.Red / 255.0, color.Green / 255.0, color.Blue / 255.0);
        }

        Close(new CanvasSizeOptions(width.Value, height.Value) { Anchor = Math.Clamp(AnchorBox.SelectedIndex, 0, 8), Fill = fill });
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
