namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http in-memory GET direct-vs-router comparison")]
public sealed partial class HttpPipelineGetComparisonBenchmarks
{
    private static readonly byte[] HelloBody = Encoding.ASCII.GetBytes("hello");
    private static readonly byte[] RequestBytes = Encoding
        .ASCII
        .GetBytes("GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n");

    private HttpConnectionHandler _directHandler = null!;
    private HttpConnectionHandler _routedHandler = null!;
    private ComparisonConnectionContext _context = null!;
    private ReadOnlySequence<byte> _buffer;

    [GlobalSetup]
    public void Setup()
    {
        _directHandler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse
                        {
                            StatusCode = 200,
                            ReasonPhrase = "OK",
                            Headers =
                            [
                                new KeyValuePair<string, string>("Content-Type", "text/plain")
                            ],
                            Body = HelloBody,
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
                        ],
                    }
                ).HandleAsync,
            }
        );

        _context = new ComparisonConnectionContext();
        _buffer = new ReadOnlySequence<byte>(RequestBytes);

        VerifyHandler(_directHandler, expectedBody: HelloBody);
        VerifyHandler(_routedHandler, expectedBody: HelloBody);
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

    private sealed class ComparisonConnectionContext : ITcpConnectionContext
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
