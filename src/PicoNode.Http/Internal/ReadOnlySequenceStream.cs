namespace PicoNode.Http.Internal;

/// <summary>
/// A read-only, non-seekable stream over a <see cref="ReadOnlySequence{T}"/>.
/// Enables streaming access to request body data without a separate byte array allocation.
/// </summary>
internal sealed class ReadOnlySequenceStream : Stream
{
    private ReadOnlySequence<byte> _sequence;
    private long _position;

    public ReadOnlySequenceStream(ReadOnlySequence<byte> sequence)
    {
        _sequence = sequence;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _sequence.Length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<int>(cancellationToken);

        try
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _sequence.Length)
            return 0;

        var remaining = _sequence.Slice(_position);
        var bytesToCopy = (int)Math.Min(buffer.Length, remaining.Length);

        var slice = remaining.Slice(0, bytesToCopy);
        slice.CopyTo(buffer);

        _position += bytesToCopy;
        return bytesToCopy;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
