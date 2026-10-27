using System.Net.Sockets;
using PicoNode;

namespace PicoWeb.Integration.Tests;

/// <summary>Shared test infrastructure for PicoWeb integration tests.</summary>
internal static class TestSupport
{
    /// <summary>
    /// The port that asks the OS for one. Binding it is the negotiation: the OS picks the port as it
    /// binds, and the port it picked is then read back from the listening socket.
    /// </summary>
    public const int OsAssignedPort = 0;

    /// <summary>
    /// Starts the server <paramref name="createServer"/> builds — with <see cref="OsAssignedPort"/> in its
    /// options — and returns it together with the port and URIs the OS actually bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test port must never be <b>probed</b> and then bound later. The helper this replaces opened a
    /// listener on port 0, read the port the OS gave it, and closed it: from that moment until the real
    /// bind the port belonged to nobody, so any process could take it — another test in the same run, a
    /// server from a previous test that had not finished shutting down, or something outside the test
    /// process entirely. The symptom was an <c>AddressInUse</c> in an unrelated test, on a port that had
    /// been free a millisecond earlier, which is the definition of a flake nobody can reproduce.
    /// </para>
    /// <para>
    /// Binding 0 makes that window empty: the port is assigned by the same syscall that takes it, so the
    /// port the server reports is one it already owns. Nothing here waits for a port to become free,
    /// retries a bind, or guesses.
    /// </para>
    /// </remarks>
    public static async Task<RunningServer> StartServerAsync(Func<int, WebServer> createServer)
    {
        ArgumentNullException.ThrowIfNull(createServer);

        var server = createServer(OsAssignedPort);
        try
        {
            await server.StartAsync();
            return new RunningServer(server, RequireBoundPort(nameof(WebServer), server.LocalEndPoint));
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts a raw <see cref="TcpNode"/> on an OS-assigned port, for the tests that drive a node below
    /// the <see cref="WebServer"/> layer. Same rule as <see cref="StartServerAsync"/>: bind 0, read the
    /// real port back.
    /// </summary>
    public static async Task<(TcpNode Node, int Port)> StartNodeAsync(Func<int, TcpNode> createNode)
    {
        ArgumentNullException.ThrowIfNull(createNode);

        var node = createNode(OsAssignedPort);
        try
        {
            await node.StartAsync();
            return (node, RequireBoundPort(nameof(TcpNode), node.LocalEndPoint));
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Reads the port out of a listening endpoint and refuses a 0, because a 0 is the silent failure mode
    /// of this whole arrangement: it is what a server reports when it hands back the endpoint it was
    /// asked for instead of the one it bound, and the first symptom would be a connection refused inside
    /// an unrelated assertion.
    /// </summary>
    public static int RequireBoundPort(string owner, EndPoint? localEndPoint)
    {
        if (localEndPoint is not IPEndPoint endpoint)
        {
            throw new InvalidOperationException(
                $"{owner} did not report a bound endpoint (LocalEndPoint was null)."
            );
        }

        if (endpoint.Port == 0)
        {
            throw new InvalidOperationException(
                $"{owner} reported port 0: it is not telling us the OS-assigned port it bound."
            );
        }

        return endpoint.Port;
    }
}

/// <summary>A started <see cref="WebServer"/> and the address it actually bound.</summary>
internal sealed class RunningServer : IAsyncDisposable
{
    public RunningServer(WebServer server, int port)
    {
        Server = server;
        Port = port;
        BaseUri = new Uri($"http://127.0.0.1:{port}");
        HttpsBaseUri = new Uri($"https://localhost:{port}");
    }

    public WebServer Server { get; }

    public int Port { get; }

    public Uri BaseUri { get; }

    public Uri HttpsBaseUri { get; }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
