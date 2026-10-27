namespace PicoWeb.DI.Tests;

/// <summary>Cross-thread "this happened" flag, set by the server side and read by the test side.</summary>
public sealed class TestFlag
{
    private int _seen;

    public bool Seen => Volatile.Read(ref _seen) != 0;

    public void Mark() => Interlocked.Exchange(ref _seen, 1);
}

/// <summary>
/// Response body stream that reports two things back to the test: that a read returned 0 (EOF) and that the stream
/// itself was disposed.
/// <para>
/// Those two facts are what identify <em>which</em> scope release ran for a streaming response, because
/// <c>ScopeBoundStream</c> releases the request scope from two different places and both end with "the probe was
/// disposed once":
/// </para>
/// <list type="bullet">
/// <item>inside the read that returns 0 — the producer is <b>not</b> disposed yet;</item>
/// <item>inside <c>ScopeBoundStream.DisposeAsync</c> — the producer is disposed <b>before</b> the scope is.</item>
/// </list>
/// <para>
/// Without the second flag, "EOF seen before disposal" is true on both routes (the producer signals EOF during the
/// read either way) and a broken EOF branch would keep the counts green.
/// </para>
/// </summary>
internal sealed class EofSignalingStream(
    byte[] payload,
    TestFlag reachedEof,
    TestFlag producerDisposed
) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Volatile.Read(ref _position);
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override int Read(Span<byte> destination)
    {
        var position = Volatile.Read(ref _position);
        var remaining = payload.Length - position;
        if (remaining <= 0)
        {
            reachedEof.Mark();
            return 0;
        }

        var read = Math.Min(remaining, destination.Length);
        payload.AsSpan(position, read).CopyTo(destination);
        Volatile.Write(ref _position, position + read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        producerDisposed.Mark();
        base.Dispose(disposing);
    }
}
