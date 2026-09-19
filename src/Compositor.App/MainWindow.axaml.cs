using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Compositor.App.Dialogs;
using Compositor.App.ViewModels;
using Compositor.Core.Project;

namespace Compositor.App;

public sealed partial class MainWindow : Window
{
    private static readonly FilePickerFileType ProjectType = new("Compositor Project") { Patterns = ["*.comp"] };
    private static readonly FilePickerFileType ImageTypes = new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = Vm;
        NewCanvasCommand = new RelayCommand(async () => await NewCanvasAsync());
        OpenCommand = new RelayCommand(async () => await OpenAsync());
        SaveCommand = new RelayCommand(async () => await SaveAsync(saveAs: false), () => Vm.HasDocument);
        SaveAsCommand = new RelayCommand(async () => await SaveAsync(saveAs: true), () => Vm.HasDocument);
        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => Vm.HasDocument);
        ExportPngCommand = new RelayCommand(async () => await ExportAsync(jpeg: false), () => Vm.HasDocument);
        ExportJpegCommand = new RelayCommand(async () => await ExportAsync(jpeg: true), () => Vm.HasDocument);
        ExitCommand = new RelayCommand(Close);
        Canvas.ImportFailed += failures => Status.Text = string.Join("  ·  ", failures);
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.HasDocument))
            {
                foreach (var command in new[] { SaveCommand, SaveAsCommand, ImportCommand, ExportPngCommand, ExportJpegCommand })
                {
                    command.Refresh();
                }
            }
        };
        Closing += async (_, e) =>
        {
            if (!Vm.Session.IsModified || _confirmedClose)
            {
                return;
            }

            e.Cancel = true;
            if (await ConfirmDiscardAsync())
            {
                _confirmedClose = true;
                Close();
            }
        };
    }

    private bool _confirmedClose;

    public EditorViewModel Vm { get; } = new();

    public RelayCommand NewCanvasCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportPngCommand { get; }
    public RelayCommand ExportJpegCommand { get; }
    public RelayCommand ExitCommand { get; }

    private async Task NewCanvasAsync()
    {
        if (Vm.Session.IsModified && !await ConfirmDiscardAsync())
        {
            return;
        }

        if (await NewCanvasWindow.ShowAsync(this) is { } size)
        {
            Vm.NewCanvas(size.Width, size.Height);
            Status.Text = $"New {size.Width}×{size.Height} canvas";
        }
    }

    private async Task OpenAsync()
    {
        if (Vm.Session.IsModified && !await ConfirmDiscardAsync())
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open Project", FileTypeFilter = [ProjectType], AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            Vm.Open(path);
            Status.Text = $"Opened {Path.GetFileName(path)}";
        }
        catch (ProjectException e)
        {
            Status.Text = e.Message;
        }
    }

    private async Task SaveAsync(bool saveAs)
    {
        var path = saveAs ? null : Vm.Path;
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Project",
                FileTypeChoices = [ProjectType],
                DefaultExtension = "comp",
                SuggestedFileName = Vm.Path is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled",
            });
            path = file?.TryGetLocalPath();
            if (path is null)
            {
                return;
            }
        }

        try
        {
            Vm.Save(path);
            Status.Text = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status.Text = e.Message;
        }
    }

    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Images", FileTypeFilter = [ImageTypes], AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var failures = Vm.Import(paths);
        Status.Text = failures.Count == 0 ? $"Imported {paths.Count} image{(paths.Count == 1 ? string.Empty : "s")}" : string.Join("  ·  ", failures);
    }

    private async Task ExportAsync(bool jpeg)
    {
        var type = jpeg ? new FilePickerFileType("JPEG") { Patterns = ["*.jpg"] } : new FilePickerFileType("PNG") { Patterns = ["*.png"] };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = jpeg ? "Export JPEG" : "Export PNG",
            FileTypeChoices = [type],
            DefaultExtension = jpeg ? "jpg" : "png",
            SuggestedFileName = Vm.Path is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled",
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(path, jpeg ? Vm.ExportJpeg(new JpegOptions()) : Vm.ExportPng());
            Status.Text = $"Exported {Path.GetFileName(path)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status.Text = e.Message;
        }
    }

    private Task<bool> ConfirmDiscardAsync() =>
        ConfirmDialog.ShowAsync(this, "Unsaved Changes", "This project has unsaved changes. Discard them?", "Discard");
}

public sealed partial class MainWindow
{
    /// <summary>
    /// Files given on the command line ("Open with", drag onto the exe): a <c>.comp</c> opens as the
    /// project; images make a canvas the size of the first and import them all.
    /// </summary>
    public void OpenFromCommandLine(string[] args)
    {
        var files = args.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            return;
        }

        var project = files.FirstOrDefault(f => string.Equals(Path.GetExtension(f), ProjectStore.Extension, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            try
            {
                Vm.Open(project);
                Status.Text = $"Opened {Path.GetFileName(project)}";
            }
            catch (ProjectException e)
            {
                Status.Text = e.Message;
            }

            return;
        }

        try
        {
            var first = ImageCodec.Decode(files[0]);
            Vm.NewCanvas(first.Width, first.Height);
            var failures = Vm.Import(files);
            Status.Text = failures.Count == 0 ? $"Imported {files.Count} image{(files.Count == 1 ? string.Empty : "s")}" : string.Join("  ·  ", failures);
        }
        catch (ImageImportException e)
        {
            Status.Text = e.Message;
        }
    }
}
