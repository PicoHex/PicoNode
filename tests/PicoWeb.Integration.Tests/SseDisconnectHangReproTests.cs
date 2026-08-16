using System.Net.Sockets;
using PicoNode;

namespace PicoWeb.Integration.Tests;

/// <summary>
/// Repro/regression suite for the PicoNode.Http bug where a streaming (SSE)
/// response whose body producer never ends (e.g. a chat-turn handler that
/// ignores connection cancellation) wedges the connection handler.
///
/// Scenario mirrors a browser "switching away" from an SSE stream:
///  1. client opens a streaming response,
///  2. client disposes/aborts the connection mid-stream,
///  3. the server releases the connection and (over HTTP/2) keeps serving
///     every other request multiplexed on that connection.
///
/// Note: Windows delivers both FIN and RST to a pending recv as 0 bytes, so a
/// transport-level distinction is impossible. Prompt release of a STALLED
/// stream therefore relies on the close backstops — the idle monitor (short
/// timeout in these tests) or an SSE keep-alive write failing — both of which
/// only take effect because the HTTP streaming read now observes the
/// connection cancellation token.
/// </summary>
public sealed class SseDisconnectHangReproTests
{
    private const int ObserveDelayMs = 1500;
    private const int OkRequestTimeoutMs = 4000;

    [Test]
    public async Task Http1_disconnect_releases_connection_and_disposes_body_stream()
    {
        DisposeTrackingStream? tracker = null;
        var (node, port) = StartServer(
            idleTimeout: TimeSpan.FromMilliseconds(500),
            trackerCapture: t => tracker = t
        );
        await node.StartAsync();

        try
        {
            // 1. Open a never-ending streaming response (SSE-like) whose
            //    producer ignores cancellation entirely.
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("GET /hang HTTP/1.1\r\nHost: localhost\r\n\r\n")
            );

            // 2. Read the response headers (chunked streaming has begun).
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer);
            await Assert.That(read).IsGreaterThan(0);

            // 3. Client "switches away": abortive disconnect, like a browser
            //    disposing an in-flight SSE fetch.
            tcp.LingerState = new LingerOption(true, 0);
            tcp.Close();
            await Task.Delay(ObserveDelayMs);

            // BUG (pre-fix): the connection is never released — the HTTP layer
            // was blocked in ReadAsync(CancellationToken.None) on a body pipe
            // that never completes. Post-fix: the idle-close cancels the
            // connection token and the streaming read unblocks.
            var metrics = node.GetMetrics();
            await Assert.That(metrics.ActiveConnections).IsEqualTo(0);

            // The HTTP layer must have disposed the body stream on exit.
            await Assert.That(tracker).IsNotNull();
            await tracker!.Disposed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await node.DisposeAsync();
        }
    }

    [Test]
    public async Task Http1_disconnect_releases_connection_when_handler_observes_cancellation()
    {
        // Same as above with a well-behaved producer (awaits ct). Guards the
        // drain/cancel ordering: the close must cancel the token even though
        // the close sequence runs after the processing task.
        var (node, port) = StartServer(
            idleTimeout: TimeSpan.FromMilliseconds(500),
            ctAwareHandler: true
        );
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
            await Assert.That(read).IsGreaterThan(0);

            tcp.LingerState = new LingerOption(true, 0);
            tcp.Close();
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

    [Test]
    public async Task Http1_half_closed_client_still_receives_its_response()
    {
        // A client that sends a request and then shuts down its send side
        // (classic half-close pattern, e.g. HTTP/1.0-style clients) must still
        // receive the full response — remote close must not cancel requests
        // that were already buffered.
        var (node, port) = StartServer();
        await node.StartAsync();

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("GET /ok HTTP/1.1\r\nHost: localhost\r\n\r\n")
            );
            tcp.Client.Shutdown(SocketShutdown.Send);

            using var reader = new StreamReader(stream, leaveOpen: true);
            var body = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(body).Contains("200");
            await Assert.That(body).Contains("ok");
        }
        finally
        {
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await node.DisposeAsync();
        }
    }

    [Test]
    public async Task Http1_stuck_connection_does_not_block_new_connections()
    {
        var (node, port) = StartServer();
        await node.StartAsync();

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await tcp.GetStream()
                .WriteAsync(
                    Encoding.ASCII.GetBytes("GET /hang HTTP/1.1\r\nHost: localhost\r\n\r\n")
                );
            var buffer = new byte[4096];
            var read = await tcp.GetStream().ReadAsync(buffer);
            await Assert.That(read).IsGreaterThan(0);
            tcp.Close();
            await Task.Delay(ObserveDelayMs);

            // A brand-new connection should still be served (per-connection isolation).
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var ok = await client
                .GetAsync("/ok")
                .WaitAsync(TimeSpan.FromMilliseconds(OkRequestTimeoutMs));
            var body = await ok.Content.ReadAsStringAsync();
            await Assert.That(body).IsEqualTo("ok");
        }
        finally
        {
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await node.DisposeAsync();
        }
    }

    [Test]
    public async Task Http2_stuck_stream_wedges_all_requests_on_the_connection()
    {
        var (node, port) = StartServer();
        await node.StartAsync();

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();

            // H2 connection preface (prior knowledge, no h2c upgrade) + SETTINGS.
            await stream.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
            await stream.WriteAsync(new byte[] { 0, 0, 0, 0x04, 0, 0, 0, 0, 0 });

            // Positive control: /ok on stream 1 must be answered (proves the
            // HPACK encoding and frame flow below are valid).
            await stream.WriteAsync(MakeHeadersFrame(1, BuildHpackHeaders("/ok"), endStream: true));
            var gotOk1 = await WaitForHeadersAsync(stream, 1, 3000);
            await Assert.That(gotOk1).IsTrue();

            // 1. Open the never-ending stream on stream 3.
            await stream.WriteAsync(
                MakeHeadersFrame(3, BuildHpackHeaders("/hang"), endStream: true)
            );
            var gotHangHeaders = await WaitForHeadersAsync(stream, 3, 3000);
            await Assert.That(gotHangHeaders).IsTrue(); // headers sent, stream stays open

            // 2. Another request on the SAME connection (browser multiplexes
            //    page/sidebar/static files onto one H2 connection).
            await stream.WriteAsync(MakeHeadersFrame(5, BuildHpackHeaders("/ok"), endStream: true));
            var gotOk2 = await WaitForHeadersAsync(stream, 5, 2500);

            // 3. Client aborts the hanging stream (browser dispose → RST_STREAM).
            //    The server must process the RST (cancelling the stream's pump)
            //    and keep serving the rest of the connection.
            await stream.WriteAsync(MakeRstStreamFrame(3));
            await stream.WriteAsync(MakeHeadersFrame(7, BuildHpackHeaders("/ok"), endStream: true));
            var gotOk3 = await WaitForHeadersAsync(stream, 7, 2500);

            // BUG (pre-fix): /ok on stream 5 (and 7) hangs because the H2 frame
            // loop is stuck inside the streaming read of the /hang stream and
            // never processes the next frames. A healthy implementation serves
            // both immediately.
            await Assert.That(gotOk2).IsTrue();
            await Assert.That(gotOk3).IsTrue();
        }
        finally
        {
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await node.DisposeAsync();
        }
    }

    private static async Task<bool> WaitForHeadersAsync(
        NetworkStream stream,
        int streamId,
        int timeoutMs
    )
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var (type, sid, _, _) = await ReadFrameAsync(stream, cts.Token);
                if (type == 0x01 && sid == streamId)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<(int Type, int StreamId, int Flags, byte[] Payload)> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken ct
    )
    {
        var header = new byte[9];
        await stream.ReadExactlyAsync(header, ct);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        var type = header[3];
        var flags = header[4];
        var streamId =
            ((header[5] & 0x7F) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return (type, streamId, flags, payload);
    }

    private static byte[] MakeHeadersFrame(int streamId, byte[] hpackBlock, bool endStream)
    {
        var flags = 0x04; // EndHeaders
        if (endStream)
        {
            flags |= 0x01;
        }

        var frame = new byte[9 + hpackBlock.Length];
        frame[0] = (byte)(hpackBlock.Length >> 16);
        frame[1] = (byte)(hpackBlock.Length >> 8);
        frame[2] = (byte)hpackBlock.Length;
        frame[3] = 0x01; // HEADERS
        frame[4] = (byte)flags;
        frame[5] = (byte)(streamId >> 24);
        frame[6] = (byte)(streamId >> 16);
        frame[7] = (byte)(streamId >> 8);
        frame[8] = (byte)streamId;
        hpackBlock.CopyTo(frame, 9);
        return frame;
    }

    private static byte[] MakeRstStreamFrame(int streamId)
    {
        // RST_STREAM with error code CANCEL (0x8).
        var frame = new byte[13];
        frame[2] = 0x04;
        frame[3] = 0x03; // RST_STREAM
        frame[5] = (byte)(streamId >> 24);
        frame[6] = (byte)(streamId >> 16);
        frame[7] = (byte)(streamId >> 8);
        frame[8] = (byte)streamId;
        frame[12] = 0x08;
        return frame;
    }

    private static byte[] BuildHpackHeaders(string path)
    {
        var block = new List<byte>();
        block.Add(0x82); // :method GET (static index 2)
        block.Add(0x44); // :path — literal with indexing, name from static index 4
        block.Add((byte)path.Length);
        block.AddRange(Encoding.ASCII.GetBytes(path));
        block.Add(0x41); // :authority — literal with indexing, name from static index 1
        block.Add(0x09);
        block.AddRange(Encoding.ASCII.GetBytes("localhost"));
        block.Add(0x86); // :scheme http (static index 6)
        return block.ToArray();
    }

    private static PicoNode.Http.HttpRequestHandler BuildHttpHandler(
        bool ctAware,
        Action<DisposeTrackingStream>? trackerCapture = null
    )
    {
        return (request, ct) =>
        {
            if (request.Path.StartsWith("/hang", StringComparison.Ordinal))
            {
                // Mimics SseEndpoint.Create + a consumer handler that never
                // ends because cancellation is not propagated to it
                // (e.g. RunSseStream blocked on an outChannel whose CTS is
                // never cancelled): the body pipe writer is never completed,
                // so the HTTP-layer read blocks forever.
                var pipe = new System.IO.Pipelines.Pipe();
                if (ctAware)
                {
                    // Well-behaved variant: the producer awaits ct. Still leaks
                    // unless the connection token is cancelled on close.
                    _ = RunProducerAsync(pipe, ct);
                }

                var tracker = new DisposeTrackingStream(pipe.Reader.AsStream());
                trackerCapture?.Invoke(tracker);

                var streaming = new PicoNode.Http.HttpResponse
                {
                    StatusCode = 200,
                    ReasonPhrase = "OK",
                    Headers =
                    [
                        new KeyValuePair<string, string>("Content-Type", "text/event-stream"),
                    ],
                    BodyStream = tracker,
                };
                return ValueTask.FromResult(streaming);
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
        };
    }

    private static async Task RunProducerAsync(System.IO.Pipelines.Pipe pipe, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation observed — complete the pipe and exit.
        }

        await pipe.Writer.CompleteAsync();
    }

    private static (TcpNode Node, int Port) StartServer(
        TimeSpan? idleTimeout = null,
        bool ctAwareHandler = false,
        Action<DisposeTrackingStream>? trackerCapture = null
    )
    {
        var port = GetRandomPort();
        var handler = new PicoNode.Http.HttpConnectionHandler(
            new PicoNode.Http.HttpConnectionHandlerOptions
            {
                RequestHandler = BuildHttpHandler(ctAwareHandler, trackerCapture),
            }
        );

        var options = new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, port),
            ConnectionHandler = handler,
        };
        if (idleTimeout is { } timeout)
        {
            options.IdleTimeout = timeout;
            options.IdleScanInterval = TimeSpan.FromMilliseconds(200);
        }

        var node = new TcpNode(options);
        return (node, port);
    }

    private static int GetRandomPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Tracks whether the HTTP layer disposed the streaming body.</summary>
    private sealed class DisposeTrackingStream : Stream
    {
        private readonly Stream _inner;
        private readonly TaskCompletionSource _disposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public DisposeTrackingStream(Stream inner) => _inner = inner;

        public Task Disposed => _disposed.Task;

        public override ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return _inner.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            _inner.Dispose();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);
    }
}
