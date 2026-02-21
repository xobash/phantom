namespace Phantom.Services;

public sealed class ExecutionCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public event EventHandler<bool>? RunningChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    public CancellationToken Begin()
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Another operation is already running.");
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
        }

        RunningChanged?.Invoke(this, true);
        return _cts.Token;
    }

    public void Complete()
    {
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _isRunning = false;
        }

        RunningChanged?.Invoke(this, false);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }
}
