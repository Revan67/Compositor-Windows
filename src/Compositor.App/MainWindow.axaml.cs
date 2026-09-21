using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Compositor.App.Dialogs;
using Compositor.App.Diagnostics;
using Compositor.App.ViewModels;
using Compositor.Core.Project;

namespace Compositor.App;

public sealed partial class MainWindow : Window
{
    private static readonly FilePickerFileType ProjectType = new("Compositor Project") { Patterns = ["*.comp"] };
    private static readonly FilePickerFileType ImageTypes = new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] };

    public MainWindow()
    {
        // Root-relative XAML bindings read these properties while InitializeComponent runs.
        // They must exist before the visual tree is loaded or the menus retain null commands.
        void Unexpected(Exception e) => Status.Text = $"Unexpected error — see {AppLog.CurrentPath}";
        NewCanvasCommand = new AsyncRelayCommand(NewCanvasAsync, onError: Unexpected, name: "File.NewCanvas");
        OpenCommand = new AsyncRelayCommand(OpenAsync, onError: Unexpected, name: "File.Open");
        SaveCommand = new AsyncRelayCommand(() => SaveAsync(saveAs: false), () => Vm.HasDocument, Unexpected, "File.Save");
        SaveAsCommand = new AsyncRelayCommand(() => SaveAsync(saveAs: true), () => Vm.HasDocument, Unexpected, "File.SaveAs");
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => Vm.HasDocument, Unexpected, "File.Import");
        ExportPngCommand = new AsyncRelayCommand(() => ExportAsync(jpeg: false), () => Vm.HasDocument, Unexpected, "File.ExportPng");
        ExportJpegCommand = new AsyncRelayCommand(() => ExportAsync(jpeg: true), () => Vm.HasDocument, Unexpected, "File.ExportJpeg");
        ExitCommand = new RelayCommand(Close, name: "File.Exit");
        InitializeComponent();
        DataContext = Vm;
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

    public AsyncRelayCommand NewCanvasCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportPngCommand { get; }
    public AsyncRelayCommand ExportJpegCommand { get; }
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
            AppLog.Info("Document", $"Created canvas {size.Width}x{size.Height}");
        }
        else
        {
            AppLog.Info("Document", "New Canvas cancelled");
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
            AppLog.Info("Document", $"Opened project {path}");
        }
        catch (ProjectException e)
        {
            Status.Text = e.Message;
            AppLog.Error("Document", $"Could not open project {path}", e);
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
            AppLog.Info("Document", $"Saved project {path}");
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status.Text = e.Message;
            AppLog.Error("Document", $"Could not save project {path}", e);
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
        AppLog.Info("Import", $"Requested={paths.Count}; succeeded={paths.Count - failures.Count}; failed={failures.Count}; files={string.Join(";", paths)}");
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
            AppLog.Info("Export", $"Exported {(jpeg ? "JPEG" : "PNG")} {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status.Text = e.Message;
            AppLog.Error("Export", $"Could not export to {path}", e);
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
