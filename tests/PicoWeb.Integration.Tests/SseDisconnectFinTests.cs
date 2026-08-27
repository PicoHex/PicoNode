using System.Net.Sockets;
using PicoNode;

namespace PicoWeb.Integration.Tests;

/// <summary>
/// Deterministic SSE disconnect detection: a client that closes the
/// connection with a graceful FIN (socket kept open, so no RST and server
/// writes keep succeeding) must cancel the SSE handler's token. This is the
/// blind spot where the idle-timeout backstop is defeated by keep-alive
/// pings (each send touches the connection, so it is never "idle").
/// Uses the DEFAULT idle timeout (2 min) so no timer backstop can satisfy
/// the 5s assertions.
/// </summary>
public sealed class SseDisconnectFinTests
{
    private const int DisconnectAssertTimeoutMs = 5000;
    private const int ObserveDelayMs = 1500;

    [Test]
    public async Task Http1_graceful_fin_cancels_sse_handler_and_releases_connection()
    {
        var handlerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var (node, port) = StartSseServer(handlerCancelled);
        await node.StartAsync();

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("GET /hang HTTP/1.1\r\nHost: localhost\r\n\r\n")
            );

            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer);
            await Assert.That(read).IsGreaterThan(0); // streaming started

            // Graceful FIN, socket left open: no RST, server writes keep
            // succeeding into kernel buffers — only the FIN can reveal the
            // disconnect.
            tcp.Client.Shutdown(SocketShutdown.Send);

            // BUG (pre-fix): the handler's token never cancels — it hangs
            // until server teardown. Post-fix: RemoteCloseToken cancels the
            // linked SSE token within milliseconds.
            await handlerCancelled.Task.WaitAsync(
                TimeSpan.FromMilliseconds(DisconnectAssertTimeoutMs)
            );
            await Task.Delay(ObserveDelayMs);
            var metrics = node.GetMetrics();
            await Assert.That(metrics.ActiveConnections).IsEqualTo(0);
        }
        finally
        {
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await node.DisposeAsync();
        }
    }

    private static (TcpNode Node, int Port) StartSseServer(TaskCompletionSource handlerCancelled)
    {
        var port = TestSupport.GetRandomPort();
        var httpHandler = new PicoNode.Http.HttpRequestHandler((request, ct) =>
        {
            if (request.Path.StartsWith("/hang", StringComparison.Ordinal))
            {
                var endpoint = SseEndpoint.Create(async (sse, ct) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        handlerCancelled.TrySetResult();
                        throw;
                    }
                });
                return endpoint(WebContext.Create(request), ct);
            }

            var body = Encoding.UTF8.GetBytes("ok");
            return ValueTask.FromResult(
                new PicoNode.Http.HttpResponse
                {
                    StatusCode = 200,
                    ReasonPhrase = "OK",
                    Headers =
                    [
                        new KeyValuePair<string, string>("Content-Length", body.Length.ToString()),
                    ],
                    Body = body,
                }
            );
        });
        var handler = new PicoNode.Http.HttpConnectionHandler(
            new PicoNode.Http.HttpConnectionHandlerOptions { RequestHandler = httpHandler }
        );
        // Default idle timeout (2 min) — intentionally NOT shortened, so the
        // assertions can only be satisfied by deterministic detection.
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = handler,
            }
        );
        return (node, port);
    }
}
