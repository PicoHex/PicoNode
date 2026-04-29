namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http in-memory end-to-end pipeline through HttpRouter")]
public sealed partial class HttpPipelineBenchmarks
{
    private static readonly byte[] HelloBody = Encoding.ASCII.GetBytes("hello");

    [Params(0, 32, 256)]
    public int BodySize { get; set; }

    private HttpConnectionHandler _handler = null!;
    private RecordingConnectionContext _context = null!;
    private ReadOnlySequence<byte> _getBuffer;
    private ReadOnlySequence<byte> _postBuffer;
    private byte[] _expectedPostBody = null!;

    [GlobalSetup]
    public void Setup()
    {
        var router = CreateRouter();

        _handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions { RequestHandler = router.HandleAsync, }
        );

        _context = new RecordingConnectionContext();
        _getBuffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n")
        );
        _postBuffer = new ReadOnlySequence<byte>(CreatePostRequestBytes(BodySize));
        _expectedPostBody = CreateEchoBody(BodySize);

        VerifyPipeline(_getBuffer, expectedStatusLine: "HTTP/1.1 200 OK", expectedBody: HelloBody);
        VerifyPipeline(
            _postBuffer,
            expectedStatusLine: "HTTP/1.1 200 OK",
            expectedBody: _expectedPostBody
        );
    }

    [IterationSetup]
    public void ResetContext() => _context.Reset();

    [Benchmark]
    public void ProcessGetThroughRouter()
    {
        _ = _handler
            .OnReceivedAsync(_context, _getBuffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public void ProcessPostEchoThroughRouter()
    {
        _ = _handler
            .OnReceivedAsync(_context, _postBuffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private void VerifyPipeline(
        ReadOnlySequence<byte> buffer,
        string expectedStatusLine,
        byte[] expectedBody
    )
    {
        _context.Reset();
        _context.EnableCapture();
        var consumed = _handler
            .OnReceivedAsync(_context, buffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (buffer.Slice(consumed).Length != 0)
        {
            throw new InvalidOperationException(
                "Expected the request to be fully consumed during setup."
            );
        }

        if (_context.SendCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one response write during setup, but observed {_context.SendCount}."
            );
        }

        if (_context.CloseCount != 0)
        {
            throw new InvalidOperationException(
                $"Expected the connection to stay open during setup, but observed {_context.CloseCount} close operations."
            );
        }

        var response = HttpResponseReader.Parse(_context.CapturedPayload);
        if (!response.StatusLine.Equals(expectedStatusLine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected status line '{expectedStatusLine}' during setup, but observed '{response.StatusLine}'."
            );
        }

        if (!response.Body.AsSpan().SequenceEqual(expectedBody))
        {
            throw new InvalidOperationException(
                "Expected response body did not match during setup."
            );
        }
    }

    private static HttpRouter CreateRouter() =>
        new(
            new HttpRouterOptions
            {
                Routes =
                [
                    HttpRoute.MapGet(
                        "/hello",
                        static (_, _) => ValueTask.FromResult(
                            new HttpResponse
                            {
                                StatusCode = 200,
                                ReasonPhrase = "OK",
                                Headers = [new KeyValuePair<string, string>("Content-Type", "text/plain")],
                                Body = HelloBody,
                            }
                        )
                    ),
                    HttpRoute.MapPost(
                        "/echo",
                        static (request, _) => ValueTask.FromResult(
                            new HttpResponse
                            {
                                StatusCode = 200,
                                ReasonPhrase = "OK",
                                Headers = [new KeyValuePair<string, string>("Content-Type", "text/plain")],
                                Body = request.Body.ToArray(),
                            }
                        )
                    ),
                ],
                FallbackHandler = static (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse { StatusCode = 404, ReasonPhrase = "Not Found", }
                    ),
            }
        );

    private static byte[] CreatePostRequestBytes(int bodySize)
    {
        var body = bodySize == 0 ? string.Empty : new string('b', bodySize);
        var request =
            $"POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: {bodySize}\r\n\r\n{body}";
        return Encoding.ASCII.GetBytes(request);
    }

    private static byte[] CreateEchoBody(int bodySize) =>
        bodySize == 0 ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(new string('b', bodySize));

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;

        public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 8081);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public byte[] CapturedPayload { get; private set; } = Array.Empty<byte>();

        private bool _capturePayload;

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_capturePayload)
            {
                CapturedPayload = buffer.ToArray();
                _capturePayload = false;
            }

            SendCount++;
            return Task.CompletedTask;
        }

        public void Close() => CloseCount++;

        public void EnableCapture() => _capturePayload = true;

        public void Reset()
        {
            SendCount = 0;
            CloseCount = 0;
            CapturedPayload = Array.Empty<byte>();
            _capturePayload = false;
        }
    }
}
