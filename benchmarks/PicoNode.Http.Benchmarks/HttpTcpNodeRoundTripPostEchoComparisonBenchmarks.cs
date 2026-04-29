namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http localhost POST echo direct-vs-router comparison")]
public sealed partial class HttpTcpNodeRoundTripPostEchoComparisonBenchmarks
{
    [Params(0, 64, 256)]
    public int BodySize { get; set; }

    private TcpNode _directNode = null!;
    private TcpNode _routedNode = null!;
    private TcpClient _directClient = null!;
    private TcpClient _routedClient = null!;
    private NetworkStream _directStream = null!;
    private NetworkStream _routedStream = null!;
    private byte[] _requestBytes = null!;
    private byte[] _expectedBody = null!;
    private byte[] _responseBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _expectedBody = CreateRequestBody(BodySize);
        _requestBytes = CreatePostRequestBytes(_expectedBody);
        _responseBuffer = CreateExpectedResponseBytes(_expectedBody);

        _directNode = CreateNode(
            static (request, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers =  [new KeyValuePair<string, string>("Content-Type", "text/plain")],
                        Body = request.Body.ToArray(),
                    }
                )
        );

        _routedNode = CreateNode(
            new HttpRouter(
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
            ).HandleAsync
        );

        _directNode.StartAsync().GetAwaiter().GetResult();
        _routedNode.StartAsync().GetAwaiter().GetResult();

        _directClient = ConnectClient(_directNode);
        _routedClient = ConnectClient(_routedNode);
        _directStream = _directClient.GetStream();
        _routedStream = _routedClient.GetStream();

        VerifyRoundTrip(_directStream, _requestBytes, _expectedBody);
        VerifyRoundTrip(_routedStream, _requestBytes, _expectedBody);
    }

    [Benchmark(Baseline = true)]
    public void DirectHandler()
    {
        _directStream.Write(_requestBytes);
        ReadExact(_directStream, _responseBuffer);
    }

    [Benchmark]
    public void RoutedHandler()
    {
        _routedStream.Write(_requestBytes);
        ReadExact(_routedStream, _responseBuffer);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _directStream?.Dispose();
        _routedStream?.Dispose();
        _directClient?.Dispose();
        _routedClient?.Dispose();
        _directNode?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _routedNode?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static TcpNode CreateNode(HttpRequestHandler requestHandler) =>
        new(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = new HttpConnectionHandler(
                    new HttpConnectionHandlerOptions { RequestHandler = requestHandler, }
                ),
                DrainTimeout = TimeSpan.FromSeconds(2),
            }
        );

    private static TcpClient ConnectClient(TcpNode node)
    {
        if (node.LocalEndPoint is not IPEndPoint localEndPoint)
        {
            throw new InvalidOperationException(
                "TcpNode did not expose an IPEndPoint after startup."
            );
        }

        var client = new TcpClient();
        client.ConnectAsync(IPAddress.Loopback, localEndPoint.Port).GetAwaiter().GetResult();
        return client;
    }

    private static void VerifyRoundTrip(
        NetworkStream stream,
        byte[] requestBytes,
        byte[] expectedBody
    )
    {
        stream.Write(requestBytes);
        var response = HttpResponseReader.Read(stream);

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

    private static byte[] CreateExpectedResponseBytes(byte[] body)
    {
        var header = Encoding
            .ASCII
            .GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n"
            );
        var response = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, response, 0, header.Length);
        Buffer.BlockCopy(body, 0, response, header.Length, body.Length);
        return new byte[response.Length];
    }

    private static void ReadExact(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "Connection closed while reading benchmark response bytes."
                );
            }

            offset += read;
        }
    }
}
