using System.IO;
using System.IO.Compression;

namespace PicoNode.Web.Tests;

public sealed class CompressionMiddlewareTests
{
    [Test]
    public async Task Compresses_with_gzip_when_accepted()
    {
        var body = Encoding.UTF8.GetBytes("Hello, world! This is a test body for compression.");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(TextResponse(body)),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(GetHeader(response, "Content-Encoding")).IsEqualTo("gzip");
        await Assert.That(response.Body.Length).IsGreaterThan(0);

        var decompressed = Decompress(response.Body, "gzip");
        await Assert
            .That(Encoding.UTF8.GetString(decompressed))
            .IsEqualTo("Hello, world! This is a test body for compression.");
    }

    [Test]
    public async Task Compresses_with_brotli_when_accepted()
    {
        var body = Encoding.UTF8.GetBytes("Hello, world! This is a test body for brotli.");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "br, gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(TextResponse(body)),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Encoding")).IsEqualTo("br");

        var decompressed = Decompress(response.Body, "br");
        await Assert
            .That(Encoding.UTF8.GetString(decompressed))
            .IsEqualTo("Hello, world! This is a test body for brotli.");
    }

    [Test]
    public async Task Compresses_with_deflate_when_only_deflate_accepted()
    {
        var body = Encoding.UTF8.GetBytes("deflate test body");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "deflate");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(TextResponse(body)),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Encoding")).IsEqualTo("deflate");

        var decompressed = Decompress(response.Body, "deflate");
        await Assert.That(Encoding.UTF8.GetString(decompressed)).IsEqualTo("deflate test body");
    }

    [Test]
    public async Task Does_not_compress_when_no_accept_encoding()
    {
        var body = Encoding.UTF8.GetBytes("plain body");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", acceptEncoding: null);

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(TextResponse(body)),
            CancellationToken.None
        );

        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("plain body");
        await Assert.That(GetHeader(response, "Content-Encoding")).IsNull();
    }

    [Test]
    public async Task Does_not_compress_empty_body()
    {
        var middleware = new CompressionMiddleware();
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(WebResults.Empty(204)),
            CancellationToken.None
        );

        await Assert.That(response.Body.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Compresses_body_stream_when_content_length_is_large_enough()
    {
        var body = Encoding.UTF8.GetBytes("streamed body that should be compressed");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers =
                        [
                            new KeyValuePair<string, string>("Content-Type", "text/plain"),
                            new KeyValuePair<string, string>("Content-Length", body.Length.ToString()),
                        ],
                        BodyStream = new MemoryStream(body),
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Encoding")).IsEqualTo("gzip");
        await Assert.That(GetHeader(response, "Content-Length")).IsNull();
        await Assert.That(response.Body.IsEmpty).IsTrue();
        await Assert.That(response.BodyStream).IsNotNull();

        var compressed = await ReadAllBytesAsync(response.BodyStream!);
        var decompressed = Decompress(compressed, "gzip");
        await Assert.That(Encoding.UTF8.GetString(decompressed)).IsEqualTo(
            "streamed body that should be compressed"
        );
    }

    [Test]
    public async Task Does_not_compress_body_stream_without_content_length()
    {
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers =
                        [new KeyValuePair<string, string>("Content-Type", "text/plain")],
                        BodyStream = new MemoryStream(Encoding.UTF8.GetBytes("stream")),
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Encoding")).IsNull();
        await Assert.That(response.BodyStream).IsNotNull();
    }

    [Test]
    public async Task Does_not_recompress_when_content_encoding_already_present()
    {
        var body = Encoding.UTF8.GetBytes("already encoded");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers =
                        [
                            new KeyValuePair<string, string>("Content-Type", "text/plain"),
                            new KeyValuePair<string, string>("Content-Encoding", "gzip"),
                            new KeyValuePair<string, string>("Content-Length", body.Length.ToString()),
                        ],
                        Body = body,
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Encoding")).IsEqualTo("gzip");
        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("already encoded");
    }

    [Test]
    public async Task Removes_stale_content_length_header()
    {
        var body = Encoding.UTF8.GetBytes("some content");
        var middleware = new CompressionMiddleware(minimumBodySize: 0);
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(TextResponse(body)),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Length")).IsNull();
    }

    [Test]
    public async Task Skips_compression_when_body_below_minimum_size()
    {
        var body = Encoding.UTF8.GetBytes("small");
        var middleware = new CompressionMiddleware(minimumBodySize: 100);
        var context = CreateContext("GET", "/", "gzip");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => ValueTask.FromResult(TextResponse(body)),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Content-Encoding")).IsNull();
        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("small");
    }

    [Test]
    public async Task SelectEncoding_prefers_brotli_over_gzip()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept-Encoding"] = "gzip, br, deflate",
        };

        var result = CompressionMiddleware.SelectEncoding(headers);

        await Assert.That(result).IsEqualTo("br");
    }

    [Test]
    public async Task SelectEncoding_honors_zero_quality_values()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept-Encoding"] = "br;q=0, gzip;q=1, deflate;q=0",
        };

        var result = CompressionMiddleware.SelectEncoding(headers);

        await Assert.That(result).IsEqualTo("gzip");
    }

    private static WebContext CreateContext(string method, string target, string? acceptEncoding)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (acceptEncoding is not null)
        {
            headers.Add(new KeyValuePair<string, string>("Accept-Encoding", acceptEncoding));
        }

        return WebContext.Create(
            new HttpRequest
            {
                Method = method,
                Target = target,
                HeaderFields = headers,
                Headers = headers.ToDictionary(
                    h => h.Key,
                    h => h.Value,
                    StringComparer.OrdinalIgnoreCase
                ),
            }
        );
    }

    private static HttpResponse TextResponse(byte[] body) =>
        new()
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers =
            [
                new KeyValuePair<string, string>("Content-Type", "text/plain"),
                new KeyValuePair<string, string>("Content-Length", body.Length.ToString()),
            ],
            Body = body,
        };

    private static string? GetHeader(HttpResponse response, string name) =>
        response
            .Headers
            .FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value;

    private static byte[] Decompress(ReadOnlyMemory<byte> compressed, string encoding)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var decompressor = encoding switch
        {
            "br" => (Stream)new BrotliStream(input, CompressionMode.Decompress),
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }

    private static async Task<ReadOnlyMemory<byte>> ReadAllBytesAsync(Stream stream)
    {
        await using (stream)
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            return output.ToArray();
        }
    }
}
