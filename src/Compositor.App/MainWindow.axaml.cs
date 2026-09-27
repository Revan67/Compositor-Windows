using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using System.Text.Json;
using Compositor.App.Dialogs;
using Compositor.App.Diagnostics;
using Compositor.App.Updates;
using Compositor.App.ViewModels;
using Compositor.Core.Document;
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
        CanvasSizeCommand = new AsyncRelayCommand(CanvasSizeAsync, () => Vm.HasDocument, Unexpected, "Image.CanvasSize");
        ImageSizeCommand = new AsyncRelayCommand(ImageSizeAsync, () => Vm.HasDocument, Unexpected, "Image.ImageSize");
        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(manual: true), onError: Unexpected, name: "Help.CheckForUpdates");
        OpenLogsCommand = new RelayCommand(OpenLogs, name: "Help.OpenLogs");
        ExitCommand = new RelayCommand(Close, name: "File.Exit");
        InitializeComponent();
        DataContext = Vm;
        _recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _recoveryTimer.Tick += async (_, _) => await SaveRecoveryIfNeededAsync();
        _recoveryTimer.Start();
        _recoveryDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _recoveryDebounceTimer.Tick += async (_, _) =>
        {
            _recoveryDebounceTimer.Stop();
            await SaveRecoveryIfNeededAsync();
        };
        Vm.Session.Changed += ScheduleRecoverySave;
        Canvas.ImportFailed += failures => Status.Text = string.Join("  ·  ", failures);
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.HasDocument))
            {
                foreach (var command in new[] { SaveCommand, SaveAsCommand, ImportCommand, ExportPngCommand, ExportJpegCommand, CanvasSizeCommand, ImageSizeCommand })
                {
                    command.Refresh();
                }
            }
        };
        Closing += async (_, e) =>
        {
            if (!Vm.Session.IsModified || _confirmedClose)
            {
                _recoveryTimer.Stop();
                _recoveryDebounceTimer.Stop();
                ClearRecovery();
                return;
            }

            e.Cancel = true;
            if (await ConfirmDiscardAsync())
            {
                _confirmedClose = true;
                _recoveryTimer.Stop();
                _recoveryDebounceTimer.Stop();
                ClearRecovery();
                Close();
            }
        };
    }

    private bool _confirmedClose;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly DispatcherTimer _recoveryDebounceTimer;
    private CanvasDocument? _lastRecoveryDocument;
    private bool _recoverySaving;
    private int _recoveryEpoch;
    private bool _checkingForUpdates;
    private readonly UpdateService _updateService = new();

    public EditorViewModel Vm { get; } = new();

    public AsyncRelayCommand NewCanvasCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportPngCommand { get; }
    public AsyncRelayCommand ExportJpegCommand { get; }
    public AsyncRelayCommand CanvasSizeCommand { get; }
    public AsyncRelayCommand ImageSizeCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand ExitCommand { get; }

    private void OpenLogs()
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AppLog.LogDirectory) { UseShellExecute = true });
        Status.Text = $"Logs: {AppLog.LogDirectory}";
        AppLog.Info("Diagnostics", "Opened logs folder");
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates)
        {
            return;
        }

        _checkingForUpdates = true;
        try
        {
            if (manual) Status.Text = "Checking for updates…";
            AppLog.Info("Updater", $"Checking GitHub Releases; current={_updateService.CurrentVersion}; manual={manual}");
            var release = await _updateService.CheckAsync();
            if (release is null)
            {
                if (manual) Status.Text = $"Compositor {_updateService.CurrentVersion} is up to date";
                AppLog.Info("Updater", "No applicable newer release found");
                return;
            }

            AppLog.Info("Updater", $"Update available; tag={release.Tag}; prerelease={release.IsPreRelease}; asset={release.Package.Name}");
            var channel = release.IsPreRelease ? "prerelease" : "release";
            var download = await ConfirmDialog.ShowAsync(this, "Compositor Update Available",
                $"{release.Name} is available ({channel}). Download the verified portable update now?\n\nYour current folder will not be changed. Compositor will stage the ZIP and ask you to close the app before replacing files.", "Download");
            if (!download)
            {
                Status.Text = $"Update {release.Tag} available";
                return;
            }

            Status.Text = $"Downloading {release.Tag}…";
            var progress = new Progress<double>(value => Status.Text = $"Downloading {release.Tag}… {value:P0}");
            var path = await _updateService.DownloadAsync(release, progress);
            Status.Text = $"Verified update downloaded: {Path.GetFileName(path)}";
            AppLog.Info("Updater", $"Verified update staged at {path}");
            var open = await ConfirmDialog.ShowAsync(this, "Update Ready",
                $"The update was downloaded and its SHA-256 checksum was verified.\n\nClose Compositor, extract the ZIP, and replace the old portable folder. Your projects and diagnostic logs are stored separately.\n\n{path}", "Open Folder", "Later");
            if (open)
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add($"/select,{path}");
                Process.Start(start);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("Updater", "Update check or download failed", e);
            if (manual) Status.Text = $"Could not check for updates: {e.Message}";
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private async Task NewCanvasAsync()
    {
        if (Vm.Session.IsModified && !await ConfirmDiscardAsync())
        {
            return;
        }

        if (await NewCanvasWindow.ShowAsync(this) is { } size)
        {
            Vm.NewCanvas(size.Width, size.Height);
            ClearRecovery();
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
            ClearRecovery();
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
            ClearRecovery();
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
        if (paths.Count > failures.Count)
        {
            // An imported image is high-value work and should not wait for the periodic checkpoint.
            _recoveryDebounceTimer.Stop();
            await SaveRecoveryIfNeededAsync();
        }
    }

    private async Task ExportAsync(bool jpeg)
    {
        var options = await ExportOptionsWindow.ShowAsync(this, jpeg);
        if (options is null)
        {
            return;
        }

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
            var bytes = jpeg
                ? Vm.ExportJpeg(new JpegOptions(options.Value.JpegQuality / 100.0))
                : Vm.ExportPng(new PngOptions(options.Value.PngCompression));
            await File.WriteAllBytesAsync(path, bytes);
            Status.Text = $"Exported {Path.GetFileName(path)}";
            AppLog.Info("Export", $"Exported {(jpeg ? $"JPEG quality={options.Value.JpegQuality}" : $"PNG compression={options.Value.PngCompression}")} {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status.Text = e.Message;
            AppLog.Error("Export", $"Could not export to {path}", e);
        }
    }

    private async Task CanvasSizeAsync()
    {
        if (Vm.Session.Document is not { } document || await CanvasSizeWindow.ShowAsync(this, document) is not { } options)
        {
            return;
        }

        try
        {
            Vm.Session.ApplyCanvasSize(options);
            Status.Text = $"Canvas resized to {options.Width}×{options.Height}";
            AppLog.Info("Document", $"Canvas size applied: {options.Width}x{options.Height}; anchor={options.Anchor}; fill={options.Fill is not null}");
        }
        catch (ProjectException e)
        {
            Status.Text = e.Message;
            AppLog.Error("Document", "Canvas size failed", e);
        }
    }

    private async Task ImageSizeAsync()
    {
        if (Vm.Session.Document is not { } document || await ImageSizeWindow.ShowAsync(this, document) is not { } options)
        {
            return;
        }

        try
        {
            Vm.Session.ApplyImageSize(options);
            Status.Text = $"Image resized to {options.Width}×{options.Height}";
            AppLog.Info("Document", $"Image size applied: {options.Width}x{options.Height}; resolution={options.Resolution:0.##}; sampling={options.Sampling}");
        }
        catch (ProjectException e)
        {
            Status.Text = e.Message;
            AppLog.Error("Document", "Image size failed", e);
        }
    }

    private Task<bool> ConfirmDiscardAsync() =>
        ConfirmDialog.ShowAsync(this, "Unsaved Changes", "This project has unsaved changes. Discard them?", "Discard");

    public async Task<bool> RecoverIfAvailableAsync()
    {
        if (!RecoveryStore.Exists)
        {
            return false;
        }

        var recover = await ConfirmDialog.ShowAsync(this, "Recover Unsaved Work", "Compositor found work from an interrupted session. Recover it as an unsaved project? Choosing Discard permanently removes the recovery copy.", "Recover", "Discard");
        if (!recover)
        {
            ClearRecovery();
            AppLog.Info("Recovery", "User discarded recovery data");
            return false;
        }

        try
        {
            var (snapshot, info) = await Task.Run(() => RecoveryStore.Load());
            Vm.Recover(snapshot);
            _lastRecoveryDocument = Vm.Session.Document;
            var source = info.SourcePath is { } path ? $" from {Path.GetFileName(path)}" : string.Empty;
            Status.Text = $"Recovered unsaved work{source} — use Save As to keep it";
            AppLog.Info("Recovery", $"Recovered autosave; savedAt={info.SavedAtUtc:O}; source={info.SourcePath ?? "untitled"}");
            return true;
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            Status.Text = $"Recovery failed — data retained at {RecoveryStore.ProjectPath}";
            AppLog.Error("Recovery", "Could not load recovery data; files retained", e);
            return false;
        }
    }

    internal async Task SaveRecoveryIfNeededAsync()
    {
        var document = Vm.Session.Document;
        if (_recoverySaving || !Vm.Session.IsModified || Vm.Session.IsBusy || document is null || ReferenceEquals(document, _lastRecoveryDocument))
        {
            return;
        }

        _recoverySaving = true;
        var epoch = _recoveryEpoch;
        try
        {
            var snapshot = ProjectSnapshot.From(document, Vm.Session.ActiveLayerId);
            await Task.Run(() => RecoveryStore.Save(snapshot, Vm.Path));
            if (epoch != _recoveryEpoch)
            {
                RecoveryStore.Clear();
                return;
            }

            _lastRecoveryDocument = document;
            AppLog.Info("Recovery", $"Autosaved modified document; layers={document.Layers.Count}; canvas={document.Width}x{document.Height}; source={Vm.Path ?? "untitled"}");
        }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Recovery", "Autosave failed", e);
        }
        finally
        {
            _recoverySaving = false;
        }
    }

    private void ScheduleRecoverySave()
    {
        if (!Vm.Session.IsModified || Vm.Session.Document is null)
        {
            return;
        }

        // Preserve committed work shortly after an edit. The longer periodic timer remains as a
        // fallback when a modal tool or another busy operation delays this one-shot save.
        _recoveryDebounceTimer.Stop();
        _recoveryDebounceTimer.Start();
    }

    private void ClearRecovery()
    {
        _recoveryDebounceTimer.Stop();
        _recoveryEpoch++;
        _lastRecoveryDocument = null;
        RecoveryStore.Clear();
    }
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
                ClearRecovery();
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
            ClearRecovery();
            var failures = Vm.Import(files);
            Status.Text = failures.Count == 0 ? $"Imported {files.Count} image{(files.Count == 1 ? string.Empty : "s")}" : string.Join("  ·  ", failures);
        }
        catch (ImageImportException e)
        {
            Status.Text = e.Message;
        }
    }
}
