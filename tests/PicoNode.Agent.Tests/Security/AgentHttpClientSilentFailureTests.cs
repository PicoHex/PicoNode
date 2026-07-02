
namespace PicoNode.Agent.Tests.Security;

/// <summary>
/// AgentHttpClient.GetMessagesAsync previously wrapped its HTTP call in a
/// blanket <c>catch (HttpRequestException) { return []; }</c>. The consequence:
/// a caller could not distinguish "the session exists and has no messages"
/// from "the agent process is down / the network is broken / the endpoint
/// returned 500". UIs, hosted services and integration tests all silently
/// treat a hard failure as an empty session, which erases evidence that the
/// backend is broken.
///
/// Fix contract: transport / connectivity failures MUST propagate to the
/// caller as an exception, mirroring how every other Get*Async method on
/// AgentHttpClient behaves (ListSessionsAsync, ListModelsAsync,
/// GetHealthAsync all propagate). Only genuinely empty payloads may return
/// an empty list.
/// </summary>
public class AgentHttpClientSilentFailureTests
{
    [Test]
    public async Task GetMessagesAsync_ThrowsWhenBackendUnreachable_InsteadOfReturningEmpty()
    {
        // Reserve an ephemeral port, then close the socket. The port is
        // almost certainly still unused a few milliseconds later; connect()
        // returns "connection refused" which HttpClient surfaces as
        // HttpRequestException.
        int port;
        using (
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        )
        {
            sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)sock.LocalEndPoint!).Port;
        }

        await using var client = new AgentHttpClient($"http://127.0.0.1:{port}");

        // Currently returns [] silently — this assertion must be a throw
        // (the old code returned an empty list and hid the failure entirely).
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.GetMessagesAsync("s1");
        });
        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GetMessagesAsync_ThrowsOn500_InsteadOfReturningEmpty()
    {
        // Spin up a trivial HttpListener that always returns 500 on the
        // first request. This mimics an agent process that is alive but
        // internally broken — pre-fix code returned [] and completely hid
        // the fault.
        int port;
        using (
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        )
        {
            sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)sock.LocalEndPoint!).Port;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var acceptTask = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            { /* listener stopped */
            }
            catch (ObjectDisposedException)
            { /* listener stopped */
            }
        });

        await using var client = new AgentHttpClient($"http://127.0.0.1:{port}");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.GetMessagesAsync("s1");
        });
        await Assert.That(ex).IsNotNull();

        listener.Stop();
        await acceptTask;
    }
}
