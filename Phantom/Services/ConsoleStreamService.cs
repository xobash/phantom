using Phantom.Models;

namespace Phantom.Services;

public sealed class ConsoleStreamService
{
    private readonly object _gate = new();
    private readonly List<PowerShellOutputEvent> _events = new();

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

    public void Publish(string stream, string text)
    {
        var evt = new PowerShellOutputEvent
        {
            Stream = stream,
            Text = text,
            Timestamp = DateTimeOffset.Now
        };

        lock (_gate)
        {
            _events.Add(evt);
            if (_events.Count > 10000)
            {
                _events.RemoveRange(0, 1000);
            }
        }

        MessageReceived?.Invoke(this, evt);
    }

    public string BuildFullLogText()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _events.Select(e => $"[{e.Timestamp:HH:mm:ss}] [{e.Stream}] {e.Text}"));
        }
    }
}
