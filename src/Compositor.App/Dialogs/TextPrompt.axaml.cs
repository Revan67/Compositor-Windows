using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Compositor.App.Dialogs;

public sealed partial class TextPrompt : Window
{
    public TextPrompt() => InitializeComponent();

    public static async Task<string?> ShowAsync(Window owner, string title, string initial)
    {
        var prompt = new TextPrompt { Title = title };
        prompt.Input.Text = initial;
        prompt.Opened += (_, _) =>
        {
            prompt.Input.Focus();
            prompt.Input.SelectAll();
        };
        return await prompt.ShowDialog<string?>(owner);
    }

    private void Accept(object? sender, RoutedEventArgs e) => Close(Input.Text);

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
