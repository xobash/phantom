namespace Phantom.Services;

public sealed class ExecutionCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private EventHandler<bool>? _runningChanged;

    public event EventHandler<bool>? RunningChanged
    {
        add
        {
            lock (_gate)
            {
                _runningChanged += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _runningChanged -= value;
            }
        }
    }

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
        EventHandler<bool>? runningChanged;
        CancellationToken token;

        lock (_gate)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Another operation is already running.");
            }

            _cts?.Dispose();
            _isRunning = true;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            runningChanged = _runningChanged;
        }

        runningChanged?.Invoke(this, true);
        return token;
    }

    public void Complete()
    {
        EventHandler<bool>? runningChanged;

        lock (_gate)
        {
            if (!_isRunning && _cts is null)
            {
                return;
            }

            _cts?.Dispose();
            _cts = null;
            _isRunning = false;
            runningChanged = _runningChanged;
        }

        runningChanged?.Invoke(this, false);
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
        }

        cts?.Cancel();
    }
}
