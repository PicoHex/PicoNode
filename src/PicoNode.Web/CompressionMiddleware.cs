namespace PicoNode.Web;

using System.Buffers;
using System.IO.Pipelines;

public sealed class CompressionMiddleware
{
    private const int DefaultMinimumBodySize = 860;
    private const string ContentLengthHeaderName = "Content-Length";
    private const string ContentEncodingHeaderName = "Content-Encoding";
    private const string VaryHeaderName = "Vary";
    private const string AcceptEncodingHeaderValue = "Accept-Encoding";

    private readonly CompressionLevel _level;
    private readonly int _minimumBodySize;

    public CompressionMiddleware(
        CompressionLevel level = CompressionLevel.Fastest,
        int minimumBodySize = DefaultMinimumBodySize
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBodySize);
        _level = level;
        _minimumBodySize = minimumBodySize;
    }

    public async ValueTask<HttpResponse> InvokeAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken
    )
    {
        var response = await next(context, cancellationToken);

        if (HasHeader(response.Headers, ContentEncodingHeaderName))
        {
            return response;
        }

        var encoding = SelectEncoding(context.Request.Headers);
        if (encoding is null)
        {
            return response;
        }

        if (response.BodyStream is not null)
        {
            if (!TryGetContentLength(response.Headers, out var contentLength))
            {
                return response;
            }

            if (contentLength < _minimumBodySize)
            {
                return response;
            }

            return new HttpResponse
            {
                StatusCode = response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Version = response.Version,
                Headers = CreateCompressedHeaders(response.Headers, encoding),
                Body = ReadOnlyMemory<byte>.Empty,
                BodyStream = new CompressedReadStream(response.BodyStream, encoding, _level),
            };
        }

        if (response.Body.Length < _minimumBodySize)
        {
            return response;
        }

        var compressed = Compress(response.Body, encoding, _level);

        return new HttpResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version,
            Headers = CreateCompressedHeaders(response.Headers, encoding),
            Body = compressed,
        };
    }

    internal static string? SelectEncoding(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue(AcceptEncodingHeaderValue, out var acceptEncoding))
        {
            return null;
        }

        ReadOnlySpan<char> remaining = acceptEncoding;
        string? bestEncoding = null;
        var bestQuality = double.NegativeInfinity;
        var bestPriority = int.MinValue;

        while (remaining.Length > 0)
        {
            var comma = remaining.IndexOf(',');
            var token = comma >= 0 ? remaining[..comma] : remaining;

            var semicolon = token.IndexOf(';');
            var encoding = (semicolon >= 0 ? token[..semicolon] : token).Trim();
            var parameters = token[(semicolon >= 0 ? semicolon : token.Length)..];

            if (!TryGetQuality(parameters, out var quality) || quality <= 0)
            {
                if (comma < 0)
                {
                    break;
                }

                remaining = remaining[(comma + 1)..];
                continue;
            }

            var candidate = GetSupportedEncoding(encoding);
            if (candidate is { } supportedEncoding)
            {
                var priority = GetEncodingPriority(supportedEncoding);
                if (quality > bestQuality || (quality == bestQuality && priority > bestPriority))
                {
                    bestEncoding = supportedEncoding;
                    bestQuality = quality;
                    bestPriority = priority;
                }
            }

            if (comma < 0)
            {
                break;
            }

            remaining = remaining[(comma + 1)..];
        }

        return bestEncoding;
    }

    private static bool HasHeader(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
    {
        foreach (var header in headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetContentLength(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        out long contentLength
    )
    {
        foreach (var header in headers)
        {
            if (
                header.Key.Equals(ContentLengthHeaderName, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(
                    header.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out contentLength
                )
                && contentLength >= 0
            )
            {
                return true;
            }
        }

        contentLength = 0;
        return false;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> CreateCompressedHeaders(
        IReadOnlyList<KeyValuePair<string, string>> sourceHeaders,
        string encoding
    )
    {
        var headers = new List<KeyValuePair<string, string>>(sourceHeaders.Count + 2);
        string? varyValue = null;

        foreach (var header in sourceHeaders)
        {
            if (header.Key.Equals(ContentLengthHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Key.Equals(ContentEncodingHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (header.Key.Equals(VaryHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                varyValue = header.Value;
                continue;
            }

            headers.Add(header);
        }

        headers.Add(new KeyValuePair<string, string>(ContentEncodingHeaderName, encoding));
        headers.Add(
            new KeyValuePair<string, string>(
                VaryHeaderName,
                MergeVaryHeader(varyValue, AcceptEncodingHeaderValue)
            )
        );

        return headers;
    }

    private static string MergeVaryHeader(string? existing, string value)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return value;
        }

        ReadOnlySpan<char> remaining = existing;
        while (remaining.Length > 0)
        {
            var comma = remaining.IndexOf(',');
            var token = (comma >= 0 ? remaining[..comma] : remaining).Trim();
            if (token.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            if (comma < 0)
            {
                break;
            }

            remaining = remaining[(comma + 1)..];
        }

        return existing + ", " + value;
    }

    private static string? GetSupportedEncoding(ReadOnlySpan<char> encoding)
    {
        if (encoding.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            return "br";
        }

        if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return "gzip";
        }

        if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
        {
            return "deflate";
        }

        return null;
    }

    private static int GetEncodingPriority(string encoding) =>
        encoding switch
        {
            "br" => 3,
            "gzip" => 2,
            "deflate" => 1,
            _ => 0,
        };

    private static bool TryGetQuality(ReadOnlySpan<char> parameters, out double quality)
    {
        quality = 1.0;
        if (parameters.IsEmpty)
        {
            return true;
        }

        ReadOnlySpan<char> remaining = parameters;
        while (remaining.Length > 0)
        {
            if (remaining[0] == ';')
            {
                remaining = remaining[1..];
            }

            var separator = remaining.IndexOf(';');
            var part = (separator >= 0 ? remaining[..separator] : remaining).Trim();

            if (part.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
            {
                return double.TryParse(
                    part[2..],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out quality
                );
            }

            if (separator < 0)
            {
                break;
            }

            remaining = remaining[(separator + 1)..];
        }

        return true;
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

    private sealed class CompressedReadStream : Stream
    {
        private readonly Stream _source;
        private readonly string _encoding;
        private readonly CompressionLevel _level;
        private readonly Pipe _pipe = new();
        private readonly CancellationTokenSource _disposeCts = new();
        private Task? _producerTask;
        private bool _disposed;

        public CompressedReadStream(Stream source, string encoding, CompressionLevel level)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _encoding = encoding;
            _level = level;
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (buffer.Length == 0)
            {
                return 0;
            }

            EnsureStarted(cancellationToken);

            while (true)
            {
                using var linkedCts = CreateLinkedToken(cancellationToken);
                var result = await _pipe.Reader.ReadAsync(linkedCts.Token);
                var available = result.Buffer;

                if (!available.IsEmpty)
                {
                    var bytesToCopy = (int)Math.Min(buffer.Length, available.Length);
                    CopySequenceToSpan(available.Slice(0, bytesToCopy), buffer.Span);
                    _pipe.Reader.AdvanceTo(available.GetPosition(bytesToCopy));
                    return bytesToCopy;
                }

                _pipe.Reader.AdvanceTo(available.End);

                if (result.IsCompleted)
                {
                    if (_producerTask is not null)
                    {
                        await _producerTask.ConfigureAwait(false);
                    }

                    return 0;
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCts.Cancel();

            if (_producerTask is null)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await _producerTask.ConfigureAwait(false);
                }
                catch
                {
                    // Surface producer failures during reads; disposal should stay best-effort.
                }
            }

            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
            _disposeCts.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        private void EnsureStarted(CancellationToken cancellationToken)
        {
            _producerTask ??= PumpCompressedAsync(cancellationToken);
        }

        private async Task PumpCompressedAsync(CancellationToken cancellationToken)
        {
            Exception? error = null;

            try
            {
                using var linkedCts = CreateLinkedToken(cancellationToken);
                await using (_source)
                {
                    await using var output = _pipe.Writer.AsStream(leaveOpen: true);
                    await using var compressor = CreateCompressor(output, _encoding, _level);
                    await _source.CopyToAsync(compressor, linkedCts.Token).ConfigureAwait(false);
                    await compressor.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                error = null;
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                await _pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
            }
        }

        private static void CopySequenceToSpan(ReadOnlySequence<byte> source, Span<byte> destination)
        {
            var copied = 0;
            foreach (var segment in source)
            {
                segment.Span.CopyTo(destination[copied..]);
                copied += segment.Length;
            }
        }

        private CancellationTokenSource CreateLinkedToken(CancellationToken cancellationToken) =>
            cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
    }
}
