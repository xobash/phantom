using Phantom.Models;
using System.Threading;
using System.Diagnostics;

namespace Phantom.Services;

public sealed class ConsoleStreamService
{
    private const int MaxEvents = 10_000;
    private const int TrimEvents = 1_000;
    private const int MaxRetainedCharacters = 2_000_000;
    private const int MaxMessageCharacters = 8_192;

    private readonly object _gate = new();
    private readonly List<PowerShellOutputEvent> _events = new();
    private readonly SynchronizationContext? _dispatchContext = SynchronizationContext.Current;
    private int _retainedCharacterCount;
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
        var safeText = NormalizeMessageText(text);
        var evt = new PowerShellOutputEvent
        {
            Stream = stream,
            Text = safeText,
            Timestamp = DateTimeOffset.Now
        };

        Func<PowerShellOutputEvent, CancellationToken, Task>? sink = null;
        lock (_gate)
        {
            _events.Add(evt);
            _retainedCharacterCount += evt.Text.Length;

            if (_events.Count > MaxEvents)
            {
                TrimOldestEvents(TrimEvents);
            }

            while (_events.Count > 0 && _retainedCharacterCount > MaxRetainedCharacters)
            {
                TrimOldestEvents(1);
            }

            sink = _persistentSink;
        }

        PublishEvent(evt);
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

    private async Task PersistAsync(Func<PowerShellOutputEvent, CancellationToken, Task> sink, PowerShellOutputEvent evt)
    {
        try
        {
            await sink(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"ConsoleStreamService persistent sink failed: {ex}");
            Publish("Warning", $"Console persistence warning: {ex.Message}", persist: false);
        }
    }

    private static string NormalizeMessageText(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length <= MaxMessageCharacters)
        {
            return value;
        }

        return value[..MaxMessageCharacters] + " ...[truncated]";
    }

    private void TrimOldestEvents(int count)
    {
        if (count <= 0 || _events.Count == 0)
        {
            return;
        }

        var trimCount = Math.Min(count, _events.Count);
        for (var i = 0; i < trimCount; i++)
        {
            _retainedCharacterCount -= _events[i].Text.Length;
        }

        _events.RemoveRange(0, trimCount);
        if (_retainedCharacterCount < 0)
        {
            _retainedCharacterCount = 0;
        }
    }

    private void PublishEvent(PowerShellOutputEvent evt)
    {
        var handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        if (_dispatchContext is not null && _dispatchContext != SynchronizationContext.Current)
        {
            _dispatchContext.Post(static state =>
            {
                var tuple = ((ConsoleStreamService Service, PowerShellOutputEvent Event))state!;
                tuple.Service.MessageReceived?.Invoke(tuple.Service, tuple.Event);
            }, (this, evt));
            return;
        }

        handler.Invoke(this, evt);
    }
}
