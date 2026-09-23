using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Compositor.App.Dialogs;
using Compositor.App.ViewModels;
using System.Collections.Specialized;

namespace Compositor.App.Panels;

public sealed partial class LayersPanel : UserControl
{
    public LayersPanel() => InitializeComponent();

    private bool _syncingSelection;
    private bool _syncQueued;
    private bool _selectionCommitQueued;
    private EditorViewModel? _rowsVm;

    private EditorViewModel? Vm => DataContext as EditorViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_rowsVm is { } old)
        {
            old.Rows.CollectionChanged -= RowsChanged;
        }

        _rowsVm = null;

        base.OnDataContextChanged(e);
        if (Vm is { } current)
        {
            _rowsVm = current;
            current.Rows.CollectionChanged += RowsChanged;
            QueueSelectionSync();
        }
    }

    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueSelectionSync();

    private void QueueSelectionSync()
    {
        if (_syncQueued)
        {
            return;
        }

        _syncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncQueued = false;
            if (Vm is not { } vm || List.SelectedItems is not { } selected)
            {
                return;
            }

            _syncingSelection = true;
            try
            {
                selected.Clear();
                foreach (var row in vm.Rows.Where(row => row.Id != vm.Session.ActiveLayerId && vm.Session.SelectedLayerIds.Contains(row.Id)))
                {
                    selected.Add(row);
                }

                if (vm.Rows.FirstOrDefault(row => row.Id == vm.Session.ActiveLayerId) is { } active)
                {
                    selected.Add(active);
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }, DispatcherPriority.Background);
    }

    private void LayerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _selectionCommitQueued)
        {
            return;
        }

        var addedPrimary = e.AddedItems.OfType<LayerRowViewModel>().LastOrDefault()?.Id;
        _selectionCommitQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionCommitQueued = false;
            // Rebuilding Rows causes transient SelectionChanged notifications while
            // ObservableCollection is still raising CollectionChanged. Let the queued
            // model-to-view sync win instead of re-entering another Rows rebuild.
            if (_syncingSelection || _syncQueued || Vm is not { } vm || List.SelectedItems is not { } selected)
            {
                return;
            }

            var rows = selected.OfType<LayerRowViewModel>().ToList();
            var primary = addedPrimary is { } id && rows.Any(row => row.Id == id)
                ? id
                : rows.LastOrDefault()?.Id;
            vm.Session.SelectLayers(rows.Select(row => row.Id), primary);
        }, DispatcherPriority.Background);
    }

    private void EyeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LayerRowViewModel row })
        {
            Vm?.Session.ToggleLayerVisibility(row.Id);
            e.Handled = true;
        }
    }

    private void OpacityPressed(object? sender, PointerPressedEventArgs e) => Vm?.Session.BeginOpacityEdit();

    private void OpacityReleased(object? sender, PointerReleasedEventArgs e) => Vm?.Session.FinishOpacityEdit();

    private async void RowDoubleTapped(object? sender, TappedEventArgs e) => await RenameSelectedAsync();

    private async void RenameClicked(object? sender, RoutedEventArgs e) => await RenameSelectedAsync();

    private async Task RenameSelectedAsync()
    {
        if (Vm?.SelectedRow is not { } row || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var name = await TextPrompt.ShowAsync(owner, "Rename Layer", row.Name);
        if (name is not null)
        {
            Vm.Session.RenameLayer(row.Id, name);
        }
    }
}
