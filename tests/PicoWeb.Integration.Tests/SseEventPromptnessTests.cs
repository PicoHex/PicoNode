using System.Net.Sockets;
using PicoNode;

namespace PicoWeb.Integration.Tests;

/// <summary>
/// SSE is a streaming protocol, so latency is part of the contract, not just the bytes: an
/// event has to reach the client when its write returns, not when the handler finishes.
/// Batching or buffering writes to save allocations would turn a live stream into one that
/// never flushes, so the real socket path is asserted here.
/// </summary>
public sealed class SseEventPromptnessTests
{
    private static readonly TimeSpan ClientBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Each_event_reaches_the_client_before_the_handler_writes_the_next()
    {
        var clientSawFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handlerFault = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var (node, port) = await StartSseServerAsync(async (sse, ct) =>
        {
            try
            {
                await sse.WriteJsonAsync("""{"seq":1}""", ct);

                // Nothing else may be written until the client has read event 1. If the
                // event were buffered until the handler finished — or until a later write
                // — this await could never be satisfied and the write below never happens.
                await clientSawFirst.Task.WaitAsync(HandlerBudget, ct);

                await sse.WriteJsonAsync("""{"seq":2}""", ct);
                await sse.CompleteAsync(ct);
            }
            catch (Exception ex)
            {
                handlerFault.TrySetResult(ex);
                throw;
            }
        });

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("GET /events HTTP/1.1\r\nHost: localhost\r\n\r\n")
            );

            var received = new StringBuilder();
            var buffer = new byte[1024];
            var deadline = DateTime.UtcNow + ClientBudget;

            while (!received.ToString().Contains(FirstEventBody) && DateTime.UtcNow < deadline)
            {
                using var readTimeout = new CancellationTokenSource(HandlerBudget);
                var read = await stream.ReadAsync(buffer, readTimeout.Token);
                if (read == 0)
                {
                    break;
                }

                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (received.ToString().Contains(FirstEventBody))
                {
                    // The client has the event: the handler may now write the next one.
                    clientSawFirst.TrySetResult();
                }
            }

            await Assert
                .That(received.ToString())
                .Contains(FirstEventBody)
                .Because("an event must reach the client without waiting for the handler to finish");

            // The handler's own account of whether the event arrived: a buffered write
            // would surface here as a TimeoutException even if the bytes eventually came.
            if (handlerFault.Task.IsCompletedSuccessfully)
            {
                throw new Exception(
                    "the SSE handler could not hand its first event to the client",
                    handlerFault.Task.Result
                );
            }

            clientSawFirst.TrySetResult();

            while (!received.ToString().Contains(SecondEventBody) && DateTime.UtcNow < deadline)
            {
                using var readTimeout = new CancellationTokenSource(HandlerBudget);
                var read = await stream.ReadAsync(buffer, readTimeout.Token);
                if (read == 0)
                {
                    break;
                }

                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            await Assert.That(received.ToString()).Contains(SecondEventBody);
            await Assert.That(received.ToString()).Contains("data: [DONE]");
        }
        finally
        {
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await node.DisposeAsync();
        }
    }

    private const string FirstEventBody = "data: {\"seq\":1}\n\n";

    private const string SecondEventBody = "data: {\"seq\":2}\n\n";

    private static Task<(TcpNode Node, int Port)> StartSseServerAsync(
        Func<SseConnection, CancellationToken, Task> sseHandler
    )
    {
        var httpHandler = new PicoNode.Http.HttpRequestHandler(
            (request, ct) =>
            {
                if (!request.Path.StartsWith("/events", StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(
                        new PicoNode.Http.HttpResponse { StatusCode = 404, ReasonPhrase = "Not Found" }
                    );
                }

                var endpoint = SseEndpoint.Create(sseHandler, keepAliveInterval: TimeSpan.Zero);
                return endpoint(WebContext.Create(request), ct);
            }
        );

        var connectionHandler = new PicoNode.Http.HttpConnectionHandler(
            new PicoNode.Http.HttpConnectionHandlerOptions { RequestHandler = httpHandler }
        );

        return TestSupport.StartNodeAsync(port =>
            new TcpNode(
                new TcpNodeOptions
                {
                    Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                    ConnectionHandler = connectionHandler,
                }
            )
        );
    }
}
