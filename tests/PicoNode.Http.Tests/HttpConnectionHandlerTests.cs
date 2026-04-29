using System.Net;
using System.Text;

namespace PicoNode.Http.Tests;

public sealed class HttpConnectionHandlerTests
{
    [Test]
    public async Task OnReceivedAsync_parses_one_request_serializes_response_and_returns_consumed_position()
    {
        var request = Encoding
            .ASCII
            .GetBytes(
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
                        Headers =  [new KeyValuePair<string, string>("Content-Type", "text/plain")],
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
        var capturedRequest =
            context.CapturedRequest
            ?? throw new InvalidOperationException("Request should be captured.");
        await Assert.That(capturedRequest.Method).IsEqualTo("POST");
        await Assert.That(capturedRequest.Target).IsEqualTo("/submit");
        await Assert.That(capturedRequest.Headers["host"]).IsEqualTo("example.com");
        await Assert
            .That(Encoding.ASCII.GetString(buffer.Slice(consumed).ToArray()))
            .IsEqualTo("NEXT");
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnReceivedAsync_keeps_incomplete_declared_bodies_buffered_without_invoking_handler()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhe"
                )
        );
        var context = new RecordingConnectionContext();
        var requestHandlerCalled = false;
        var handler = CreateHandler(
            (_, _) =>
            {
                requestHandlerCalled = true;
                return ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                );
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
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
            Encoding
                .ASCII
                .GetBytes(
                    "POST /submit HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: gzip\r\n\r\n"
                )
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /too-large HTTP/1.1\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                MaxRequestBytes = 8,
                RequestHandler = static (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                    ),
            }
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
                return ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                );
            }
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
    }

    [Test]
    public async Task OnReceivedAsync_honors_connection_close_requests()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                )
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo("HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
    }

    [Test]
    public async Task OnReceivedAsync_honors_connection_close_across_repeated_header_fields()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes(
                    "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: keep-alive\r\nConnection: close\r\n\r\n"
                )
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                )
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnReceivedAsync_maps_missing_host_requests_to_400_and_closes_connection()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nAccept: text/plain\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nHost: example.org\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: http://example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET foo HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(static (_, _) => throw new InvalidOperationException("boom"));

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
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
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        using var cancellationTokenSource = new CancellationTokenSource();
        var handler = CreateHandler(
            static (_, cancellationToken) =>
            {
                throw new OperationCanceledException(cancellationToken);
            }
        );

        cancellationTokenSource.Cancel();

        await Assert
            .That(
                async () =>
                    await handler.OnReceivedAsync(context, buffer, cancellationTokenSource.Token)
            )
            .Throws<OperationCanceledException>();
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnReceivedAsync_streams_response_body_with_chunked_encoding()
    {
        var bodyBytes = Encoding.ASCII.GetBytes("Hello, chunked world!");
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = new MemoryStream(bodyBytes),
                    }
                )
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);

        // First send is chunked headers
        var headers = Encoding.ASCII.GetString(context.AllSent[0]);
        await Assert.That(headers).Contains("Transfer-Encoding: chunked");
        await Assert.That(headers).DoesNotContain("Content-Length");

        // Concatenate body sends (chunk data + terminator)
        var bodyOutput = string.Concat(
            context.AllSent.Skip(1).Select(b => Encoding.ASCII.GetString(b))
        );
        await Assert.That(bodyOutput).Contains("15\r\nHello, chunked world!\r\n");
        await Assert.That(bodyOutput).EndsWith("0\r\n\r\n");
    }

    [Test]
    public async Task OnReceivedAsync_streaming_response_disposes_body_stream()
    {
        var stream = new TrackingMemoryStream(Encoding.ASCII.GetBytes("data"));
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = stream,
                    }
                )
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(stream.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Constructor_rejects_non_positive_streaming_response_buffer_size()
    {
        await Assert
            .That(
                () =>
                    new HttpConnectionHandler(
                        new HttpConnectionHandlerOptions
                        {
                            RequestHandler = static (_, _) =>
                                ValueTask.FromResult(
                                    new HttpResponse
                                    {
                                        StatusCode = 204,
                                        ReasonPhrase = "No Content"
                                    }
                                ),
                            StreamingResponseBufferSize = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OnReceivedAsync_streaming_response_uses_configured_buffer_size()
    {
        var stream = new ChunkRecordingStream(Encoding.ASCII.GetBytes("abcdef"));
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse
                        {
                            StatusCode = 200,
                            ReasonPhrase = "OK",
                            BodyStream = stream,
                        }
                    ),
                StreamingResponseBufferSize = 3,
            }
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(stream.ReadBufferSizes.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(stream.ReadBufferSizes.All(static size => size == 3)).IsTrue();
    }

    [Test]
    public async Task OnReceivedAsync_streaming_response_closes_connection_when_requested()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = new MemoryStream(Encoding.ASCII.GetBytes("bye")),
                    }
                )
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(context.CloseCount).IsEqualTo(1);
        var headers = Encoding.ASCII.GetString(context.AllSent[0]);
        await Assert.That(headers).Contains("Connection: close");
    }

    [Test]
    public async Task OnReceivedAsync_http10_closes_connection_by_default()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .Contains("Connection: close");
    }

    [Test]
    public async Task OnReceivedAsync_http10_keeps_alive_when_requested()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nConnection: keep-alive\r\n\r\n")
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnReceivedAsync_http10_streaming_falls_back_to_buffered()
    {
        var body = Encoding.ASCII.GetBytes("buffered body");
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n"));
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = new MemoryStream(body),
                    }
                )
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        // Should send as a single buffered response, not chunked
        await Assert.That(context.SendCount).IsEqualTo(1);
        var response = Encoding.ASCII.GetString(context.LastSent);
        await Assert.That(response).Contains("Content-Length: 13");
        await Assert.That(response).DoesNotContain("Transfer-Encoding");
        await Assert.That(response).Contains("buffered body");
    }

    [Test]
    public async Task OnReceivedAsync_sends_100_continue_when_body_incomplete()
    {
        // Headers with Expect: 100-continue, but no body yet
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"
                )
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent))
            .IsEqualTo("HTTP/1.1 100 Continue\r\n\r\n");
    }

    [Test]
    public async Task OnReceivedAsync_sends_100_continue_only_once()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"
                )
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        // First call — sends 100
        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);
        // Second call — should NOT send 100 again
        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(context.SendCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnReceivedAsync_skips_100_continue_when_body_present()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\nhello"
                )
        );
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        // Should NOT send 100, should send the final response directly
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert.That(Encoding.ASCII.GetString(context.LastSent)).Contains("HTTP/1.1 200 OK");
    }

    [Test]
    public async Task OnReceivedAsync_keeps_partial_http2_preface_buffered_without_sending()
    {
        var prefix = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n");
        var buffer = new ReadOnlySequence<byte>(prefix);
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(buffer.Length);
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnReceivedAsync_http1_path_is_unchanged_when_http2_preface_does_not_match()
    {
        var request = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: example.com\r\n\r\n");
        var buffer = new ReadOnlySequence<byte>(request);
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                )
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent))
            .Contains("HTTP/1.1 204 No Content");
    }

    [Test]
    public async Task OnReceivedAsync_switches_connection_to_websocket_mode_after_101()
    {
        var requestBytes = Encoding
            .ASCII
            .GetBytes(
                "GET /ws HTTP/1.1\r\n"
                    + "Host: localhost\r\n"
                    + "Upgrade: websocket\r\n"
                    + "Connection: Upgrade\r\n"
                    + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
                    + "Sec-WebSocket-Version: 13\r\n\r\n"
            );

        var buffer = new ReadOnlySequence<byte>(requestBytes);
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (request, _) => ValueTask.FromResult(WebSocketUpgrade.TryUpgrade(request)!)
        );

        await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        var ping = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, "ping"u8);
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(ping),
            CancellationToken.None
        );

        await Assert.That(context.SendCount).IsEqualTo(2);
        await Assert
            .That(Encoding.ASCII.GetString(context.AllSent[0]))
            .Contains("101 Switching Protocols");

        var success = WebSocketFrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.AllSent[1]),
            out var pong,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(pong!.OpCode).IsEqualTo(WebSocketOpCode.Pong);
        await Assert.That(Encoding.UTF8.GetString(pong.Payload.Span)).IsEqualTo("ping");
    }

    [Test]
    public async Task OnReceivedAsync_websocket_close_frame_sends_close_and_closes_connection()
    {
        var requestBytes = Encoding
            .ASCII
            .GetBytes(
                "GET /ws HTTP/1.1\r\n"
                    + "Host: localhost\r\n"
                    + "Upgrade: websocket\r\n"
                    + "Connection: Upgrade\r\n"
                    + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
                    + "Sec-WebSocket-Version: 13\r\n\r\n"
            );

        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            (request, _) => ValueTask.FromResult(WebSocketUpgrade.TryUpgrade(request)!)
        );

        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(requestBytes),
            CancellationToken.None
        );

        var close = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Close, []);
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(close),
            CancellationToken.None
        );

        await Assert.That(context.CloseCount).IsEqualTo(1);
        var success = WebSocketFrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.AllSent[1]),
            out var closeFrame,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(closeFrame!.OpCode).IsEqualTo(WebSocketOpCode.Close);
    }

    [Test]
    public async Task OnReceivedAsync_detects_http2_preface_and_sends_settings()
    {
        var buffer = new ReadOnlySequence<byte>(Http2FrameCodec.ClientPreface.ToArray());
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        var consumed = await handler.OnReceivedAsync(context, buffer, CancellationToken.None);

        await Assert.That(buffer.Slice(consumed).Length).IsEqualTo(0);
        await Assert.That(context.SendCount).IsEqualTo(1);
        var success = Http2FrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.LastSent),
            out var frame,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.Settings);
    }

    [Test]
    public async Task OnReceivedAsync_acks_client_settings_on_http2_connection()
    {
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(Http2FrameCodec.ClientPreface.ToArray()),
            CancellationToken.None
        );

        var settings = Http2FrameCodec.EncodeSettings(
            new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100)
        );
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(settings),
            CancellationToken.None
        );

        await Assert.That(context.SendCount).IsEqualTo(2);
        var success = Http2FrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.AllSent[1]),
            out var ack,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(ack!.Type).IsEqualTo(Http2FrameType.Settings);
        await Assert.That(ack.HasFlag(Http2FrameFlags.Ack)).IsTrue();
    }

    [Test]
    public async Task OnReceivedAsync_acks_http2_ping()
    {
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(Http2FrameCodec.ClientPreface.ToArray()),
            CancellationToken.None
        );

        var ping = Http2FrameCodec.EncodePing([1, 2, 3, 4, 5, 6, 7, 8]);
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(ping),
            CancellationToken.None
        );

        await Assert.That(context.SendCount).IsEqualTo(2);
        var success = Http2FrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.AllSent[1]),
            out var ack,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(ack!.Type).IsEqualTo(Http2FrameType.Ping);
        await Assert.That(ack.HasFlag(Http2FrameFlags.Ack)).IsTrue();
    }

    [Test]
    public async Task OnReceivedAsync_sends_goaway_when_http2_headers_frame_arrives()
    {
        var context = new RecordingConnectionContext();
        var handler = CreateHandler(
            static (_, _) => throw new InvalidOperationException("should not be called")
        );

        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(Http2FrameCodec.ClientPreface.ToArray()),
            CancellationToken.None
        );

        var headersFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            [0x82]
        );

        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(headersFrame),
            CancellationToken.None
        );

        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(context.SendCount).IsEqualTo(2);
        var success = Http2FrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.AllSent[1]),
            out var goAway,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(goAway!.Type).IsEqualTo(Http2FrameType.GoAway);
    }

    private static HttpConnectionHandler CreateHandler(HttpRequestHandler requestHandler) =>
        new(new HttpConnectionHandlerOptions { RequestHandler = requestHandler });

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public TrackingMemoryStream(byte[] buffer)
            : base(buffer) { }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ChunkRecordingStream(byte[] buffer) : Stream
    {
        private readonly byte[] _buffer = buffer;
        private int _position;

        public List<int> ReadBufferSizes { get; } = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default
        )
        {
            ReadBufferSizes.Add(destination.Length);

            if (_position >= _buffer.Length)
            {
                return ValueTask.FromResult(0);
            }

            var bytesToRead = Math.Min(2, _buffer.Length - _position);
            _buffer.AsMemory(_position, bytesToRead).CopyTo(destination);
            _position += bytesToRead;
            return ValueTask.FromResult(bytesToRead);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public HttpRequest? CapturedRequest { get; set; }

        public long ConnectionId { get; init; } = 1;

        public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public byte[] LastSent { get; private set; } = [];

        public List<byte[]> AllSent { get; } = [];

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            LastSent = buffer.ToArray();
            AllSent.Add(LastSent);
            SendCount++;
            return Task.CompletedTask;
        }

        public void Close()
        {
            CloseCount++;
        }
    }
}
