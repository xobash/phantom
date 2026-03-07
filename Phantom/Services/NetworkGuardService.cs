using System.Net.NetworkInformation;

namespace Phantom.Services;

public sealed class NetworkGuardService
{
    public bool IsOnline()
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return false;
        }

        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .ToList();
            return active.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
