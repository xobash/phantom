using Phantom.Models;

namespace Phantom.Services;

public sealed class SettingsProvider
{
    public AppSettings Current { get; private set; } = new();

    public void Update(AppSettings settings)
    {
        Current = settings;
    }
}
