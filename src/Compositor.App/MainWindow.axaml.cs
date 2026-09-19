using Avalonia.Controls;
using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Phase 0 proof: a bitmap made in Core, with a translucent square blended on top, reaches
        // the screen through Avalonia's Skia lease with no copy.
        var bitmap = Checkerboard.Create(512, 384, cellSize: 16);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(0x30, 0x90, 0xFF, 0xA0), IsAntialias = true })
        {
            canvas.DrawRoundRect(new SKRect(96, 64, 416, 320), 24, 24, paint);
        }

        Canvas.Bitmap = bitmap;
    }
}
