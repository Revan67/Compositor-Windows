using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Compositor.Core.Document;
using Compositor.Core.Project;

namespace Compositor.App.Dialogs;

public sealed partial class ImageSizeWindow : Window
{
    private double _ratio = 1;
    private bool _syncing;

    public ImageSizeWindow() => InitializeComponent();

    public static Task<ImageSizeOptions?> ShowAsync(Window owner, CanvasDocument document)
    {
        var dialog = new ImageSizeWindow { _ratio = (double)document.Width / document.Height };
        dialog._syncing = true;
        dialog.WidthBox.Value = document.Width;
        dialog.HeightBox.Value = document.Height;
        dialog.ResolutionBox.Value = (decimal)document.Resolution;
        dialog._syncing = false;
        return dialog.ShowDialog<ImageSizeOptions?>(owner);
    }

    private void WidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_syncing && LockBox.IsChecked == true && WidthBox.Value is { } width)
        {
            _syncing = true;
            HeightBox.Value = Math.Clamp(Math.Round(width / (decimal)_ratio), 1, 30_000);
            _syncing = false;
        }
    }

    private void HeightChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_syncing && LockBox.IsChecked == true && HeightBox.Value is { } height)
        {
            _syncing = true;
            WidthBox.Value = Math.Clamp(Math.Round(height * (decimal)_ratio), 1, 30_000);
            _syncing = false;
        }
    }

    private void Apply(object? sender, RoutedEventArgs e)
    {
        var width = CanvasDocument.ValidDimension(WidthBox.Value?.ToString("0") ?? string.Empty);
        var height = CanvasDocument.ValidDimension(HeightBox.Value?.ToString("0") ?? string.Empty);
        var resolution = (double)(ResolutionBox.Value ?? 0);
        if (width is null || height is null || resolution is < 1 or > 9600)
        {
            ErrorText.Text = "Enter valid dimensions and a resolution from 1 to 9,600 ppi.";
            return;
        }

        var sampling = SamplingBox.SelectedIndex switch { 0 => LayerSampling.Nearest, 1 => LayerSampling.Smooth, _ => LayerSampling.High };
        Close(new ImageSizeOptions(width.Value, height.Value, resolution) { Sampling = sampling });
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
