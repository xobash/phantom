using Phantom.Models;

namespace Phantom.Services;

public sealed class ConsoleStreamService
{
    private readonly object _gate = new();
    private readonly List<PowerShellOutputEvent> _events = new();
    private Func<PowerShellOutputEvent, CancellationToken, Task>? _persistentSink;

    public event EventHandler<PowerShellOutputEvent>? MessageReceived;

    public IReadOnlyList<PowerShellOutputEvent> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void AttachPersistentSink(Func<PowerShellOutputEvent, CancellationToken, Task> sink)
    {
        lock (_gate)
        {
            _persistentSink = sink;
        }
    }

    public void Publish(string stream, string text, bool persist = true)
    {
        var evt = new PowerShellOutputEvent
        {
            Stream = stream,
            Text = text,
            Timestamp = DateTimeOffset.Now
        };

        Func<PowerShellOutputEvent, CancellationToken, Task>? sink = null;
        lock (_gate)
        {
            _events.Add(evt);
            if (_events.Count > 10000)
            {
                _events.RemoveRange(0, 1000);
            }

            sink = _persistentSink;
        }

        MessageReceived?.Invoke(this, evt);
        if (persist && sink is not null)
        {
            _ = PersistAsync(sink, evt);
        }
    }

    public string BuildFullLogText()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _events.Select(e => $"[{e.Timestamp:HH:mm:ss}] [{e.Stream}] {e.Text}"));
        }
    }

    private static async Task PersistAsync(Func<PowerShellOutputEvent, CancellationToken, Task> sink, PowerShellOutputEvent evt)
    {
        try
        {
            await sink(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
