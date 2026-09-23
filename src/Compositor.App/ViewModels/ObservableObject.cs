using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Compositor.App.Diagnostics;

namespace Compositor.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>A command whose availability is re-evaluated whenever the session changes.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null, string? name = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            AppLog.Info("Command", $"Begin {name ?? "unnamed"}");
            try
            {
                execute();
                AppLog.Info("Command", $"End {name ?? "unnamed"}");
            }
            catch (Exception e)
            {
                AppLog.Error("Command", $"Failed {name ?? "unnamed"}", e);
                throw;
            }
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>An async command that logs its complete lifetime and prevents overlapping execution.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null, string? name = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    internal async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _running = true;
        Refresh();
        AppLog.Info("Command", $"Begin {name ?? "unnamed async"}");
        try
        {
            await execute();
            AppLog.Info("Command", $"End {name ?? "unnamed async"}");
        }
        catch (Exception e)
        {
            AppLog.Error("Command", $"Failed {name ?? "unnamed async"}", e);
            onError?.Invoke(e);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
