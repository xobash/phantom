using Phantom.Models;

namespace Phantom.Services;

public sealed class SettingsProvider
{
    private volatile AppSettings _current = new();

    public AppSettings Current => _current;

    public void Update(AppSettings settings)
    {
        Interlocked.Exchange(ref _current, settings);
    }
}
