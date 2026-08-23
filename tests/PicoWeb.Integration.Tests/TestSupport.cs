using System.Net.Sockets;

namespace PicoWeb.Integration.Tests;

/// <summary>Shared test infrastructure for PicoWeb integration tests.</summary>
internal static class TestSupport
{
    /// <summary>Gets an OS-assigned free TCP port on loopback.</summary>
    public static int GetRandomPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
