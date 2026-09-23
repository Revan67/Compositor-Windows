using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Compositor.App.Dialogs;

public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmLabel, string cancelLabel = "Cancel")
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.Message.Text = message;
        dialog.Confirm.Content = confirmLabel;
        dialog.CancelButton.Content = cancelLabel;
        return await dialog.ShowDialog<bool>(owner);
    }

    private void Accept(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
