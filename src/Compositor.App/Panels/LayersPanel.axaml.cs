using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Compositor.App.Dialogs;
using Compositor.App.ViewModels;

namespace Compositor.App.Panels;

public sealed partial class LayersPanel : UserControl
{
    public LayersPanel() => InitializeComponent();

    private EditorViewModel? Vm => DataContext as EditorViewModel;

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
