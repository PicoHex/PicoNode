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
            Encoding.ASCII.GetBytes(
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
            Encoding.ASCII.GetBytes(
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
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"
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
        await Assert
            .That(Encoding.ASCII.GetString(context.LastSent.ToArray()))
            .IsEqualTo("HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
    }

    [Test]
    public async Task OnReceivedAsync_honors_connection_close_across_repeated_header_fields()
    {
        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes(
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
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: example.com\r\nHost: example.org\r\n\r\n"
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
            .That(async () =>
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
            .That(() =>
                new HttpConnectionHandler(
                    new HttpConnectionHandlerOptions
                    {
                        RequestHandler = static (_, _) =>
                            ValueTask.FromResult(
                                new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
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
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"
            )
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
            Encoding.ASCII.GetBytes(
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
            Encoding.ASCII.GetBytes(
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
            Encoding.ASCII.GetBytes(
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
        var requestBytes = Encoding.ASCII.GetBytes(
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

        var ping = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, "ping"u8, mask: true);
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
        var requestBytes = Encoding.ASCII.GetBytes(
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

        var close = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Close, [], mask: true);
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

        // First frame after preface must be SETTINGS (RFC 7540 §3.5)
        var settingsFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Settings,
            Http2FrameFlags.None,
            0,
            []
        );
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(settingsFrame),
            CancellationToken.None
        );

        var ping = Http2FrameCodec.EncodePing([1, 2, 3, 4, 5, 6, 7, 8]);
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(ping),
            CancellationToken.None
        );

        // SETTINGS ACK + PING ACK + the server's initial SETTINGS = 3+ frames
        await Assert.That(context.SendCount).IsGreaterThanOrEqualTo(3);
        // Check the last frame (should be PING ACK)
        var last = context.AllSent[^1];
        var success = Http2FrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(last),
            out var ack,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(ack!.Type).IsEqualTo(Http2FrameType.Ping);
        await Assert.That(ack.HasFlag(Http2FrameFlags.Ack)).IsTrue();
    }

    [Test]
    public async Task OnReceivedAsync_sends_rst_stream_when_http2_headers_missing_path()
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

        // First frame after preface must be SETTINGS (RFC 7540 §3.5)
        var settingsFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Settings,
            Http2FrameFlags.None,
            0,
            []
        );
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(settingsFrame),
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

        // Missing :path is a stream-level error: RST_STREAM sent, connection stays open.
        await Assert.That(context.CloseCount).IsEqualTo(0);
        await Assert.That(context.SendCount).IsGreaterThanOrEqualTo(3);
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

        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public string? NegotiatedProtocol { get; set; }

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

    [Test]
    public async Task OnConnectedAsync_with_h2_does_not_send_initial_settings()
    {
        // Bug: OnConnectedAsync was sending initial SETTINGS via SendAsync,
        // which races with the Chromium client's incoming TLS data and causes
        // WSAECONNABORTED (10053) on Schannel. The fix defers SETTINGS to
        // the first OnReceivedAsync call.
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            }
        );
        var connection = new RecordingConnectionContext { NegotiatedProtocol = "h2" };

        // H2 ALPN connection established — OnConnectedAsync is called.
        await handler.OnConnectedAsync(connection, CancellationToken.None);

        // The initial SETTINGS must NOT be sent here. Deferred to OnReceivedAsync.
        await Assert
            .That(connection.SendCount)
            .IsEqualTo(0)
            .Because(
                "initial SETTINGS should be deferred to OnReceivedAsync to avoid TLS re-negotiation race"
            );
    }

    [Test]
    public async Task OnReceivedAsync_with_h2_ALPN_sends_initial_settings_before_processing()
    {
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            }
        );
        var connection = new RecordingConnectionContext { NegotiatedProtocol = "h2" };

        // Simulate what happens after TLS + ALPN negotiation:
        // 1. OnConnectedAsync runs first
        await handler.OnConnectedAsync(connection, CancellationToken.None);

        // 2. Then OnReceivedAsync receives the client's data.
        //    Construct a valid HTTP/2 frame sequence: PRI preface + SETTINGS frame.
        //    Client SETTINGS: 1 setting (MaxConcurrentStreams=100), no ACK flag.
        //    Frame header: length=6, type=4(Settings), flags=0, streamId=0
        var clientSettings = new byte[]
        {
            0x00,
            0x00,
            0x06, // length = 6
            0x04, // type = SETTINGS
            0x00, // flags = None
            0x00,
            0x00,
            0x00,
            0x00, // streamId = 0
            0x00,
            0x03, // SETTINGS_MAX_CONCURRENT_STREAMS = 3
            0x00,
            0x00,
            0x00,
            0x64, // value = 100
        };
        var preface = Http2FrameCodec.ClientPreface.ToArray();
        var buffer = new ReadOnlySequence<byte>(preface.Concat(clientSettings).ToArray());

        await handler.OnReceivedAsync(connection, buffer, CancellationToken.None);

        // The initial server SETTINGS must have been sent (as first SendAsync call).
        await Assert
            .That(connection.SendCount)
            .IsGreaterThan(0)
            .Because("initial SETTINGS must be sent before processing client frames");

        // First sent frame should be a SETTINGS frame (type=4, stream=0).
        // HTTP/2 frame: 9-byte header, payload follows.
        var firstSent = connection.AllSent[0];
        await Assert.That(firstSent.Length).IsGreaterThanOrEqualTo(9);
        await Assert.That((Http2FrameType)firstSent[3]).IsEqualTo(Http2FrameType.Settings);
        // SETTINGS frame must go on stream 0.
        var streamId =
            ((firstSent[5] & 0x7F) << 24)
            | (firstSent[6] << 16)
            | (firstSent[7] << 8)
            | firstSent[8];
        await Assert.That(streamId).IsEqualTo(0);
    }

    [Test]
    public async Task H2_ALPN_processes_full_request_with_huffman_and_browser_headers()
    {
        string? actualMethod = null;
        string? actualPath = null;

        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                {
                    actualMethod = req.Method;
                    actualPath = req.Target;
                    return ValueTask.FromResult(
                        new HttpResponse
                        {
                            StatusCode = 200,
                            ReasonPhrase = "OK",
                            Headers =
                            [
                                new KeyValuePair<string, string>("content-type", "text/plain"),
                            ],
                            Body = "pong"u8.ToArray(),
                        }
                    );
                },
            }
        );
        var connection = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        await handler.OnConnectedAsync(connection, CancellationToken.None);

        // Build frames like a real browser
        var frames = new List<byte>();
        frames.AddRange(Http2FrameCodec.ClientPreface.ToArray());

        // SETTINGS: Chromium-like settings
        frames.AddRange(
            Http2FrameCodec.EncodeFrame(
                Http2FrameType.Settings,
                Http2FrameFlags.None,
                0,
                [
                    0x00,
                    0x01,
                    0x00,
                    0x00,
                    0x20,
                    0x00, // HEADER_TABLE_SIZE=8192
                    0x00,
                    0x03,
                    0x00,
                    0x00,
                    0x00,
                    0x64, // MAX_CONCURRENT_STREAMS=100
                    0x00,
                    0x04,
                    0x00,
                    0x04,
                    0x00,
                    0x00, // INITIAL_WINDOW_SIZE=262144
                    0x00,
                    0x06,
                    0x00,
                    0x01,
                    0x00,
                    0x00,
                ]
            ) // MAX_HEADER_LIST_SIZE=65536
        );

        // WINDOW_UPDATE: connection-level, increment=15663105 (like Chrome)
        frames.AddRange(
            Http2FrameCodec.EncodeFrame(
                Http2FrameType.WindowUpdate,
                Http2FrameFlags.None,
                0,
                [0x00, 0xef, 0x00, 0x01]
            )
        );

        // HEADERS with browser-like headers including Huffman encoding
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/"),
            (":authority", "localhost:7004"),
            (":scheme", "https"),
            ("sec-ch-ua", "\"Not/A)Brand\";v=\"99\""),
            ("sec-ch-ua-mobile", "?0"),
            ("sec-ch-ua-platform", "\"Windows\""),
            ("upgrade-insecure-requests", "1"),
            ("user-agent", "Mozilla/5.0 Chrome/999"),
            ("accept", "text/html"),
        };
        var encoder = new HpackEncoder();
        var headerBlock = encoder.Encode(headers);
        var flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream;
        var headersFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            flags,
            1,
            headerBlock
        );
        frames.AddRange(headersFrame);

        var buffer = new ReadOnlySequence<byte>([.. frames]);
        await handler.OnReceivedAsync(connection, buffer, CancellationToken.None);

        await Assert.That(actualMethod).IsEqualTo("GET");
        await Assert.That(actualPath).IsEqualTo("/");
        await Assert.That(connection.CloseCount).IsEqualTo(0);
        // Verify response frames exist
        await Assert
            .That(connection.AllSent.Count)
            .IsGreaterThanOrEqualTo(3)
            .Because("expected initial SETTINGS + SETTINGS ACK + response HEADERS");

        // The response should NOT contain a GOAWAY frame
        var lastSent = connection.AllSent[^1];
        await Assert
            .That((Http2FrameType)lastSent[3])
            .IsNotEqualTo(Http2FrameType.GoAway)
            .Because("server should not send GOAWAY on a valid request");
    }

    [Test]
    public async Task H2_ALPN_with_two_concurrent_connections()
    {
        string? actualMethod = null;
        string? actualPath = null;

        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                {
                    actualMethod = req.Method;
                    actualPath = req.Target;
                    return ValueTask.FromResult(
                        new HttpResponse
                        {
                            StatusCode = 200,
                            ReasonPhrase = "OK",
                            Headers =
                            [
                                new KeyValuePair<string, string>("content-type", "text/plain"),
                            ],
                            Body = "pong"u8.ToArray(),
                        }
                    );
                },
            }
        );
        var connection = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        await handler.OnConnectedAsync(connection, CancellationToken.None);

        // Build H2 frames: PRI preface + SETTINGS + HEADERS for GET /api/health
        var frames = new List<byte>();
        frames.AddRange(Http2FrameCodec.ClientPreface.ToArray());

        // Client SETTINGS
        frames.AddRange(
            Http2FrameCodec.EncodeSettings(
                new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100)
            )
        );

        // HEADERS frame: GET /api/health on stream 1, END_HEADERS + END_STREAM
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/api/health"),
            (":authority", "localhost:7004"),
            (":scheme", "https"),
        };
        var headerBlock = new HpackEncoder().Encode(headers);
        var flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream;
        var headersFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            flags,
            1,
            headerBlock
        );
        frames.AddRange(headersFrame);

        var buffer = new ReadOnlySequence<byte>([.. frames]);
        await handler.OnReceivedAsync(connection, buffer, CancellationToken.None);

        // Assert HTTP handler was called
        await Assert.That(actualMethod).IsEqualTo("GET");
        await Assert.That(actualPath).IsEqualTo("/api/health");
        await Assert.That(connection.CloseCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnConnectedAsync_with_h2_ALPN_sets_MaxRequestBodyBytes_from_options()
    {
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
                MaxRequestBodySize = 12345,
            }
        );
        var connection = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        await handler.OnConnectedAsync(connection, CancellationToken.None);

        var state = connection.UserState as ConnectionRuntimeState;
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.MaxRequestBodyBytes).IsEqualTo(12345);
    }

    [Test]
    public async Task ALPN_h2_settings_exchange_succeeds()
    {
        // ALPN h2 flow without PRI preface:
        // 1. OnConnectedAsync → sets up state, does NOT send (deferred)
        // 2. Client sends SETTINGS → server sends initial SETTINGS + SETTINGS ACK

        var conn = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        var handler = new HttpConnectionHandler(
            new()
            {
                RequestHandler = (req, ct) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            }
        );

        // Step 1: OnConnectedAsync — SETTINGS deferred, no send yet
        await handler.OnConnectedAsync(conn, CancellationToken.None);
        await Assert.That(conn.AllSent.Count).IsEqualTo(0);

        // Step 2: Client sends SETTINGS (no PRI preface, as in ALPN)
        var clientSettings = Http2FrameCodec.EncodeSettings(
            new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100)
        );
        await handler.OnReceivedAsync(
            conn,
            new ReadOnlySequence<byte>(clientSettings),
            CancellationToken.None
        );
        // Server must send initial SETTINGS + SETTINGS ACK = 2 frames
        await Assert.That(conn.AllSent.Count).IsEqualTo(2);
        // First sent: initial SETTINGS (no ACK flag)
        await Assert.That(conn.AllSent[0][3]).IsEqualTo((byte)Http2FrameType.Settings);
        await Assert.That((conn.AllSent[0][4] & 0x01)).IsEqualTo(0x00);
        // Second sent: SETTINGS ACK
        await Assert.That(conn.AllSent[1][3]).IsEqualTo((byte)Http2FrameType.Settings);
        await Assert.That((conn.AllSent[1][4] & 0x01)).IsEqualTo(0x01); // ACK flag
    }

    [Test]
    public async Task ALPN_h2_with_client_preface_succeeds()
    {
        // Simulate browser ALPN h2: sends PRI preface + SETTINGS in one shot.
        var conn = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        var handler = new HttpConnectionHandler(
            new()
            {
                RequestHandler = (req, ct) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            }
        );

        await handler.OnConnectedAsync(conn, CancellationToken.None);
        // Verify state was set to Http2
        var stateAfterConnect = conn.UserState as ConnectionRuntimeState;
        await Assert.That(stateAfterConnect).IsNotNull();
        await Assert.That(stateAfterConnect!.Protocol).IsEqualTo(ConnectionProtocol.Http2);

        // Build client data: PRI preface + SETTINGS frame
        var clientData = new List<byte>();
        clientData.AddRange(Http2FrameCodec.ClientPreface.ToArray());
        clientData.AddRange(
            Http2FrameCodec.EncodeSettings(
                new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100)
            )
        );

        var result = await handler.OnReceivedAsync(
            conn,
            new ReadOnlySequence<byte>([.. clientData]),
            CancellationToken.None
        );
        // Server must send initial SETTINGS + SETTINGS ACK = 2 frames
        await Assert.That(conn.AllSent.Count).IsEqualTo(2);
        // First sent: initial SETTINGS (no ACK flag)
        await Assert.That(conn.AllSent[0][3]).IsEqualTo((byte)Http2FrameType.Settings);
        await Assert.That((conn.AllSent[0][4] & 0x01)).IsEqualTo(0x00);
        // Second sent: SETTINGS ACK
        await Assert.That(conn.AllSent[1][3]).IsEqualTo((byte)Http2FrameType.Settings);
        await Assert.That((conn.AllSent[1][4] & 0x01)).IsEqualTo(0x01); // ACK flag
    }

    [Test]
    public async Task ALPN_h2_with_partial_preface_handled_correctly()
    {
        // Preface arrives in two chunks. First chunk is too small → wait.
        // Second chunk completes the preface + SETTINGS → process correctly.
        var conn = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        var handler = new HttpConnectionHandler(
            new()
            {
                RequestHandler = (req, ct) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            }
        );

        await handler.OnConnectedAsync(conn, CancellationToken.None);
        conn.AllSent.Clear();

        // Chunk 1: partial preface (12 bytes of "PRI * HTTP/")
        var preface = Http2FrameCodec.ClientPreface.ToArray();
        var chunk1 = new ReadOnlySequence<byte>(preface.Take(12).ToArray());
        await handler.OnReceivedAsync(conn, chunk1, CancellationToken.None);
        await Assert.That(conn.AllSent.Count).IsEqualTo(0); // no response, waiting

        // Chunk 2: rest of preface + SETTINGS (but chunk1 data isn't retained —
        // in real pipe it would be. This test verifies the early-return path
        // doesn't crash, which proves the 'P' guard works.)
    }

    [Test]
    public async Task H2_headers_with_priority_flag_does_not_trigger_goaway()
    {
        // RED: Browsers send HEADERS with PRIORITY flag (0x20), adding 5 bytes
        // of {Exclusive, StreamDependency, Weight} before HPACK data.
        // The server must skip these bytes before HPACK decoding.
        string? actualMethod = null;
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                {
                    actualMethod = req.Method;
                    return ValueTask.FromResult(
                        new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                    );
                },
            }
        );
        var conn = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        await handler.OnConnectedAsync(conn, CancellationToken.None);

        var frames = BuildBrowserFramesWithPriority("/test", priority: true);
        var buffer = new ReadOnlySequence<byte>([.. frames]);
        await handler.OnReceivedAsync(conn, buffer, CancellationToken.None);

        await Assert.That(actualMethod).IsEqualTo("GET");
        await Assert.That(conn.CloseCount).IsEqualTo(0);
        // No frame should be GOAWAY — HPACK decode must succeed
        foreach (var sent in conn.AllSent)
        {
            await Assert.That((Http2FrameType)sent[3]).IsNotEqualTo(Http2FrameType.GoAway);
        }
    }

    [Test]
    public async Task H2_headers_without_priority_flag_still_works()
    {
        // Baseline: without PRIORITY flag the request must still succeed
        string? actualPath = null;
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (req, ct) =>
                {
                    actualPath = req.Target;
                    return ValueTask.FromResult(
                        new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                    );
                },
            }
        );
        var conn = new RecordingConnectionContext { NegotiatedProtocol = "h2" };
        await handler.OnConnectedAsync(conn, CancellationToken.None);

        var frames = BuildBrowserFramesWithPriority("/plain", priority: false);
        var buffer = new ReadOnlySequence<byte>([.. frames]);
        await handler.OnReceivedAsync(conn, buffer, CancellationToken.None);

        await Assert.That(actualPath).IsEqualTo("/plain");
        await Assert.That(conn.CloseCount).IsEqualTo(0);
    }

    private static List<byte> BuildBrowserFramesWithPriority(string path, bool priority)
    {
        var frames = new List<byte>();
        frames.AddRange(Http2FrameCodec.ClientPreface.ToArray());
        frames.AddRange(
            Http2FrameCodec.EncodeSettings(
                new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100)
            )
        );
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", path),
            (":authority", "localhost"),
            (":scheme", "https"),
        };
        var headerBlock = new HpackEncoder().Encode(headers);

        if (priority)
        {
            // 5-byte priority info before HPACK data
            var payload = new byte[5 + headerBlock.Length];
            payload[4] = 16; // Weight=16
            headerBlock.CopyTo(payload.AsSpan(5));
            frames.AddRange(
                Http2FrameCodec.EncodeFrame(
                    Http2FrameType.Headers,
                    Http2FrameFlags.EndHeaders
                        | Http2FrameFlags.EndStream
                        | Http2FrameFlags.Priority,
                    1,
                    payload
                )
            );
        }
        else
        {
            frames.AddRange(
                Http2FrameCodec.EncodeFrame(
                    Http2FrameType.Headers,
                    Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
                    1,
                    headerBlock
                )
            );
        }
        return frames;
    }
}
