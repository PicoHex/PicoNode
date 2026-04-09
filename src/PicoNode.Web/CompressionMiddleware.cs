using System.IO;
using System.IO.Compression;

namespace PicoNode.Web;

public sealed class CompressionMiddleware
{
    private readonly CompressionLevel _level;

    public CompressionMiddleware(CompressionLevel level = CompressionLevel.Fastest)
    {
        _level = level;
    }

    public async ValueTask<HttpResponse> InvokeAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken
    )
    {
        var response = await next(context, cancellationToken);

        if (response.Body.IsEmpty)
        {
            return response;
        }

        var encoding = SelectEncoding(context.Request.Headers);
        if (encoding is null)
        {
            return response;
        }

        var compressed = Compress(response.Body, encoding, _level);

        var headers = new List<KeyValuePair<string, string>>(response.Headers.Count + 2);
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers.Add(header);
        }

        headers.Add(new KeyValuePair<string, string>("Content-Encoding", encoding));

        return new HttpResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version,
            Headers = headers,
            Body = compressed,
        };
    }

    internal static string? SelectEncoding(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Accept-Encoding", out var acceptEncoding))
        {
            return null;
        }

        ReadOnlySpan<char> remaining = acceptEncoding;
        var hasBr = false;
        var hasGzip = false;
        var hasDeflate = false;

        while (remaining.Length > 0)
        {
            var comma = remaining.IndexOf(',');
            var token = comma >= 0 ? remaining[..comma] : remaining;

            var semicolon = token.IndexOf(';');
            var encoding = (semicolon >= 0 ? token[..semicolon] : token).Trim();

            if (encoding.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                hasBr = true;
            }
            else if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                hasGzip = true;
            }
            else if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
            {
                hasDeflate = true;
            }

            if (comma < 0)
            {
                break;
            }

            remaining = remaining[(comma + 1)..];
        }

        if (hasBr)
        {
            return "br";
        }

        if (hasGzip)
        {
            return "gzip";
        }

        if (hasDeflate)
        {
            return "deflate";
        }

        return null;
    }

    private static byte[] Compress(
        ReadOnlyMemory<byte> body,
        string encoding,
        CompressionLevel level
    )
    {
        using var output = new MemoryStream();

        using (var compressor = CreateCompressor(output, encoding, level))
        {
            compressor.Write(body.Span);
        }

        return output.ToArray();
    }

    private static Stream CreateCompressor(
        Stream output,
        string encoding,
        CompressionLevel level
    ) =>
        encoding switch
        {
            "br" => new BrotliStream(output, level, leaveOpen: true),
            "gzip" => new GZipStream(output, level, leaveOpen: true),
            "deflate" => new DeflateStream(output, level, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
}
