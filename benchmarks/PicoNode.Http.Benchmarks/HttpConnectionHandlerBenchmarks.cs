namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http HttpConnectionHandler request processing")]
public sealed partial class HttpConnectionHandlerBenchmarks
{
    private static readonly byte[] PongBody = Encoding.ASCII.GetBytes("pong");

    [Params(0, 32, 256, 1024)]
    public int BodySize { get; set; }

    private HttpConnectionHandler _handler = null!;
    private RecordingConnectionContext _context = null!;
    private ReadOnlySequence<byte> _buffer;
    private static readonly HttpResponse Response =
        new()
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers =  [new KeyValuePair<string, string>("Content-Type", "text/plain")],
            Body = PongBody,
        };

    [GlobalSetup]
    public void Setup()
    {
        var requestBytes = CreateRequestBytes(BodySize);
        _buffer = new ReadOnlySequence<byte>(requestBytes);

        _handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (_, _) => ValueTask.FromResult(Response),
            }
        );

        _context = new RecordingConnectionContext();

        var consumed = _handler
            .OnReceivedAsync(_context, _buffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var remainingLength = _buffer.Slice(consumed).Length;

        if (remainingLength != 0)
        {
            throw new InvalidOperationException(
                $"Expected the request to be fully consumed during setup, but {remainingLength} bytes remained."
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

        _context.Reset();
    }

    [IterationSetup]
    public void ResetContext() => _context.Reset();

    [Benchmark]
    public void ProcessRequest()
    {
        _ = _handler
            .OnReceivedAsync(_context, _buffer, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static byte[] CreateRequestBytes(int bodySize)
    {
        var body = bodySize == 0 ? string.Empty : new string('a', bodySize);
        var request =
            $"POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: {bodySize}\r\n\r\n{body}";
        return Encoding.ASCII.GetBytes(request);
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;

        public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 8080);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            SendCount++;
            return Task.CompletedTask;
        }

        public void Close() => CloseCount++;

        public void Reset()
        {
            SendCount = 0;
            CloseCount = 0;
        }
    }
}
