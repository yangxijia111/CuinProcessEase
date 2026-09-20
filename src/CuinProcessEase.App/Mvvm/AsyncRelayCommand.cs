using System.Windows.Input;

namespace CuinProcessEase.App.Mvvm;

/// <summary>
/// 轻量异步命令：执行期间 IsRunning 置位并阻止重入。
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter)
        => !IsRunning && (_canExecute?.Invoke() ?? true);

    /// <summary>可 await 的执行入口（防重入）。</summary>
    public async Task ExecuteAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            IsRunning = true;
            await _execute(_cts.Token);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async void Execute(object? parameter)
        => await ExecuteAsync();
}
