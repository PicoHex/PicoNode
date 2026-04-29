namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http in-memory POST echo direct-vs-router comparison")]
public sealed partial class HttpPipelinePostEchoComparisonBenchmarks
{
    [Params(0, 32, 256)]
    public int BodySize { get; set; }

    private HttpConnectionHandler _directHandler = null!;
    private HttpConnectionHandler _routedHandler = null!;
    private ComparisonConnectionContext _context = null!;
    private ReadOnlySequence<byte> _buffer;
    private byte[] _expectedBody = null!;

    [GlobalSetup]
    public void Setup()
    {
        _expectedBody = CreateRequestBody(BodySize);

        _directHandler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (request, _) =>
                    ValueTask.FromResult(
                        new HttpResponse
                        {
                            StatusCode = 200,
                            ReasonPhrase = "OK",
                            Headers =
                            [
                                new KeyValuePair<string, string>("Content-Type", "text/plain")
                            ],
                            Body = request.Body.ToArray(),
                        }
                    ),
            }
        );

        _routedHandler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = new HttpRouter(
                    new HttpRouterOptions
                    {
                        Routes =
                        [
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
                    }
                ).HandleAsync,
            }
        );

        _context = new ComparisonConnectionContext();
        _buffer = new ReadOnlySequence<byte>(CreatePostRequestBytes(_expectedBody));

        VerifyHandler(_directHandler, _expectedBody);
        VerifyHandler(_routedHandler, _expectedBody);
    }

    [IterationSetup]
    public void ResetContext() => _context.Reset();

    [Benchmark(Baseline = true)]
    public void DirectHandler()
    {
        _ = _directHandler
            .OnReceivedAsync(_context, _buffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public void RoutedHandler()
    {
        _ = _routedHandler
            .OnReceivedAsync(_context, _buffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private void VerifyHandler(HttpConnectionHandler handler, byte[] expectedBody)
    {
        _context.Reset();
        _context.EnableCapture();

        var consumed = handler
            .OnReceivedAsync(_context, _buffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (_buffer.Slice(consumed).Length != 0)
        {
            throw new InvalidOperationException(
                "Expected the request to be fully consumed during setup."
            );
        }

        if (_context.SendCount != 1 || _context.CloseCount != 0)
        {
            throw new InvalidOperationException(
                "Expected one response write and no connection close during setup."
            );
        }

        var response = HttpResponseReader.Parse(_context.CapturedPayload);
        if (!response.StatusLine.Equals("HTTP/1.1 200 OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected 200 OK during setup, but observed '{response.StatusLine}'."
            );
        }

        if (!response.Body.AsSpan().SequenceEqual(expectedBody))
        {
            throw new InvalidOperationException(
                "Expected response body did not match during setup."
            );
        }
    }

    private static byte[] CreateRequestBody(int bodySize)
    {
        var body = new byte[bodySize];
        for (var index = 0; index < body.Length; index++)
        {
            body[index] = (byte)('a' + (index % 26));
        }

        return body;
    }

    private static byte[] CreatePostRequestBytes(byte[] body)
    {
        var header = Encoding
            .ASCII
            .GetBytes(
                $"POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n"
            );
        var request = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, request, 0, header.Length);
        Buffer.BlockCopy(body, 0, request, header.Length, body.Length);
        return request;
    }

    private sealed class ComparisonConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;

        public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 8082);

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
