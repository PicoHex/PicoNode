namespace PicoNode.Web.Internal;

/// <summary>
/// Wraps a response BodyStream so the request DI scope stays alive until the
/// stream is fully consumed (disposed by the HTTP processor). Without this,
/// streaming responses (SSE etc.) outlive the scope that created them.
/// </summary>
internal sealed class ScopeBoundStream : Stream
{
    private readonly Stream _inner;
    private readonly IAsyncDisposable _scope;
    private bool _disposed; // inner stream disposed
    private bool _scopeReleased; // scope disposed (separate flag: EOF may release it before DisposeAsync)

    public ScopeBoundStream(Stream inner, IAsyncDisposable scope)
    {
        _inner = inner;
        _scope = scope;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken ct = default
    )
    {
        var read = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (read == 0 && !_scopeReleased)
        {
            // EOF: the body is fully consumed — release the scope now.
            _scopeReleased = true;
            await _scope.DisposeAsync().ConfigureAwait(false);
        }
        return read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken ct = default
    ) => ValueTask.FromException(new NotSupportedException());

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _inner.Dispose();
            if (!_scopeReleased)
            {
                _scopeReleased = true;
                _ = _scope.DisposeAsync().AsTask(); // sync dispose path: fire-and-forget scope release
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            if (!_scopeReleased)
            {
                _scopeReleased = true;
                await _scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
