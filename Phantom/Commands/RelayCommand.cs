using System.Windows;
using System.Windows.Input;

namespace Phantom.Commands;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke((T?)parameter) ?? true;
    }

    public void Execute(object? parameter)
    {
        _execute((T?)parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        _cts = new CancellationTokenSource();

        try
        {
            await _execute(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowCommandFailure(ex);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }

    private static void ShowCommandFailure(Exception ex)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            MessageBox.Show(ex.ToString(), "Phantom Command Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
            MessageBox.Show(ex.ToString(), "Phantom Command Error", MessageBoxButton.OK, MessageBoxImage.Error)));
    }
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, CancellationToken, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public AsyncRelayCommand(Func<T?, CancellationToken, Task> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke((T?)parameter) ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        _cts = new CancellationTokenSource();

        try
        {
            await _execute((T?)parameter, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowCommandFailure(ex);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));
    }

    private static void ShowCommandFailure(Exception ex)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            MessageBox.Show(ex.ToString(), "Phantom Command Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
            MessageBox.Show(ex.ToString(), "Phantom Command Error", MessageBoxButton.OK, MessageBoxImage.Error)));
    }
}
