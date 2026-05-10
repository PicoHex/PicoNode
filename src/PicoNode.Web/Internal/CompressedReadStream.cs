namespace PicoNode.Web.Internal;

internal sealed class CompressedReadStream : Stream
{
    private readonly Stream _source;
    private readonly string _encoding;
    private readonly CompressionLevel _level;
    private readonly ILogger? _logger;
    private readonly Pipe _pipe = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _producerTask;
    private bool _disposed;

    public CompressedReadStream(Stream source, string encoding, CompressionLevel level, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _encoding = encoding;
        _level = level;
        _logger = logger;
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

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
        await _disposeCts.CancelAsync();

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
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Debug, new EventId(0), "Producer task faulted during compression stream dispose", ex);
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
                await using var compressor = CompressionMiddleware.CreateCompressor(
                    output,
                    _encoding,
                    _level
                );
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
