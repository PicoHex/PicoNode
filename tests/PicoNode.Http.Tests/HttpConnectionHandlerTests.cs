using System.Net;
using System.Text;

namespace PicoNode.Http.Tests;

public sealed class HttpConnectionHandlerTests
{
    [Test]
    public async Task OnReceivedAsync_parses_one_request_serializes_response_and_returns_consumed_position()
    {
        var request = Encoding.ASCII.GetBytes(
            "POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhelloNEXT"
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            requestHandler: (request, _) =>
            {
                context.CapturedRequest = request;
                return ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers = [new KeyValuePair<string, string>("Content-Type", "text/plain")],
                        Body = Encoding.ASCII.GetBytes("pong"),
                    }
                );
            }
        );

        var buffer = new ReadOnlySequence<byte>(request);
        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(context.CapturedRequest).IsNotNull();
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert.That(context.CloseCount).IsEqualTo(0);
        var capturedRequest = context.CapturedRequest ?? throw new InvalidOperationException("Request should be captured.");
        await Assert.That(capturedRequest.Method).IsEqualTo("POST");
        await Assert.That(capturedRequest.Target).IsEqualTo("/submit");
        await Assert.That(capturedRequest.Headers["host"]).IsEqualTo("example.com");
        await Assert.That(Encoding.ASCII.GetString(buffer.Slice(consumed).ToArray())).IsEqualTo("NEXT");
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/plain\r\n"
                    + "Content-Length: 4\r\n"
                    + "\r\n"
                    + "pong"
            );
    }

    [Test]
    public async Task OnReceivedAsync_returns_buffer_start_for_incomplete_input_without_sending()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnReceivedAsync_keeps_incomplete_declared_bodies_buffered_without_invoking_handler()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhe")
        );
        var context = new RecordingConnectionContext();
        var requestHandlerCalled = false;
        var handler = CreateHandler(
            (_, _) =>
            {
                requestHandlerCalled = true;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" });
            }
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(requestHandlerCalled).IsFalse();
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnReceivedAsync_maps_malformed_requests_to_400_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET /bad\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 400 Bad Request\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_unsupported_framing_to_501_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("POST /submit HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 501 Not Implemented\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_oversized_requests_to_413_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET /too-large HTTP/1.1\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                MaxRequestBytes = 8,
                RequestHandler = static (_, _) => ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                ),
            }
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 413 Payload Too Large\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_keeps_connection_open_by_default()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (request, _) =>
            {
                context.CapturedRequest = request;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" });
            }
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
    }

    [Test]
    public async Task OnReceivedAsync_honors_connection_close_requests()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_honors_connection_close_across_repeated_header_fields()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: keep-alive\r\nConnection: close\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnReceivedAsync_maps_missing_host_requests_to_400_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nAccept: text/plain\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 400 Bad Request\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_duplicate_host_requests_to_400_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nHost: example.org\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 400 Bad Request\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_invalid_header_values_to_400_and_closes_connection()
    {
        var prefix = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nX-Test: ok");
        var suffix = Encoding.ASCII.GetBytes("bad\r\n\r\n");
        var bytes = new byte[prefix.Length + 1 + suffix.Length];

        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0x01;
        suffix.CopyTo(bytes, prefix.Length + 1);

        var buffer = new ReadOnlySequence<byte>(bytes);
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 400 Bad Request\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_invalid_host_formats_to_400_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: http://example.com\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 400 Bad Request\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_invalid_request_targets_to_400_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET foo HTTP/1.1\r\nHost: example.com\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("should not be called"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 400 Bad Request\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_maps_request_handler_failures_to_500_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("boom"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 500 Internal Server Error\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task OnReceivedAsync_preserves_cancellation_from_request_handler()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"));
        var context = new RecordingConnectionContext();
        using var cancellationTokenSource = new CancellationTokenSource();
        var handler = CreateHandler(
            static (_, cancellationToken) =>
            {
                throw new OperationCanceledException(cancellationToken);
            }
        );

        cancellationTokenSource.Cancel();

        await Assert.That(
            async () => await handler.OnReceivedAsync(context, buffer, cancellationTokenSource.Token)
        ).Throws<OperationCanceledException>();
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    private static HttpConnectionHandler CreateHandler(HttpRequestHandler requestHandler) =>
        new(new HttpConnectionHandlerOptions { RequestHandler = requestHandler });

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public HttpRequest? CapturedRequest { get; set; }

        public long ConnectionId { get; init; } = 1;

        public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public byte[] LastSent { get; private set; } = [];

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken = default)
        {
            LastSent = buffer.ToArray();
            SendCount++;
            return Task.CompletedTask;
        }

        public void Close()
        {
            CloseCount++;
        }
    }
}
