namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http TcpNode localhost round-trip")]
public sealed partial class HttpTcpNodeRoundTripBenchmarks
{
    private static readonly byte[] HelloBody = Encoding.ASCII.GetBytes("hello");

    [Params(0, 64, 256)]
    public int BodySize { get; set; }

    private TcpNode _node = null!;
    private TcpClient _client = null!;
    private NetworkStream _stream = null!;
    private byte[] _getRequest = null!;
    private byte[] _postRequest = null!;
    private byte[] _expectedGetBody = null!;
    private byte[] _expectedPostBody = null!;
    private int _expectedGetResponseLength;
    private int _expectedPostResponseLength;
    private byte[] _getResponseBuffer = null!;
    private byte[] _postResponseBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var router = CreateRouter();
        _expectedGetBody = HelloBody;
        _expectedPostBody = CreateRequestBody(BodySize);

        _node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = new HttpConnectionHandler(
                    new HttpConnectionHandlerOptions { RequestHandler = router.HandleAsync, }
                ),
                DrainTimeout = TimeSpan.FromSeconds(2),
            }
        );

        _node.StartAsync().GetAwaiter().GetResult();

        if (_node.LocalEndPoint is not IPEndPoint localEndPoint)
        {
            throw new InvalidOperationException(
                "TcpNode did not expose an IPEndPoint after startup."
            );
        }

        _client = new TcpClient();
        _client.ConnectAsync(IPAddress.Loopback, localEndPoint.Port).GetAwaiter().GetResult();
        _stream = _client.GetStream();

        _getRequest = Encoding.ASCII.GetBytes("GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n");
        _postRequest = CreatePostRequestBytes(BodySize);
        _expectedGetResponseLength = CreateExpectedResponseBytes(HelloBody).Length;
        _expectedPostResponseLength = CreateExpectedResponseBytes(_expectedPostBody).Length;
        _getResponseBuffer = new byte[_expectedGetResponseLength];
        _postResponseBuffer = new byte[_expectedPostResponseLength];

        VerifyRoundTrip(_getRequest, expectedStatusLine: "HTTP/1.1 200 OK", _expectedGetBody);
        VerifyRoundTrip(_postRequest, expectedStatusLine: "HTTP/1.1 200 OK", _expectedPostBody);
    }

    [Benchmark]
    public void RoundTripGet()
    {
        _stream.Write(_getRequest);
        ReadExact(_stream, _getResponseBuffer);
    }

    [Benchmark]
    public void RoundTripPostEcho()
    {
        _stream.Write(_postRequest);
        ReadExact(_stream, _postResponseBuffer);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stream?.Dispose();
        _client?.Dispose();

        if (_node is not null)
        {
            _node.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void VerifyRoundTrip(
        byte[] requestBytes,
        string expectedStatusLine,
        byte[] expectedBody
    )
    {
        _stream.Write(requestBytes);
        var response = HttpResponseReader.Read(_stream);

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
            }
        );

    private static byte[] CreatePostRequestBytes(int bodySize)
    {
        var body = CreateRequestBody(bodySize);
        var header = Encoding
            .ASCII
            .GetBytes(
                $"POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: {body.Length}\r\n\r\n"
            );
        var request = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, request, 0, header.Length);
        Buffer.BlockCopy(body, 0, request, header.Length, body.Length);
        return request;
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
        return response;
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
