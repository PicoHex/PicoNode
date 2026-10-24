using PicoNode.Web.Internal;

namespace PicoNode.Web.Tests;

/// <summary>
/// Direct tests for <see cref="ScopeBoundStream"/>: it owns the lifetime of a request DI scope once the scope
/// has been handed to a streaming response body, and it releases it from more than one place (EOF, sync
/// dispose, async dispose). Every one of those paths has to release it <b>exactly once</b> — a missing release
/// leaks the scope for the life of the process, a second release is a double-dispose of user services — and
/// none of them used to be tested except through the whole HTTP pipeline.
/// </summary>
public sealed class ScopeBoundStreamTests
{
    private static readonly byte[] Payload = "0123456789"u8.ToArray();

    [Test]
    public async Task Read_async_returning_data_does_not_release_the_scope()
    {
        var scope = new ProbeScope();
        await using var stream = new ScopeBoundStream(new ScriptedStream(Payload), scope);

        var buffer = new byte[Payload.Length];
        var read = await stream.ReadAsync(buffer);

        await Assert.That(read).IsEqualTo(Payload.Length);
        await Assert
            .That(scope.Disposals)
            .IsEqualTo(0)
            .Because("a response that is still streaming must keep its scope alive");
    }

    [Test]
    public async Task Read_async_returning_zero_releases_the_scope()
    {
        var scope = new ProbeScope();
        var inner = new ScriptedStream(Payload);
        await using var stream = new ScopeBoundStream(inner, scope);

        var buffer = new byte[Payload.Length];
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(Payload.Length);
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(0); // EOF

        await Assert.That(scope.Disposals).IsEqualTo(1);
        await Assert
            .That(inner.Disposed)
            .IsEqualTo(0)
            .Because("EOF releases the scope, not the producer: disposing the body is what disposes the stream");
    }

    [Test]
    public async Task Sync_Dispose_releases_the_scope()
    {
        var scope = new ProbeScope();
        var inner = new ScriptedStream(Payload);
        var stream = new ScopeBoundStream(inner, scope);

        stream.Dispose();

        // The sync path is fire-and-forget (an IAsyncDisposable cannot be awaited from Dispose), so the
        // probe is awaited rather than read — and it is awaited with a deadline, so a release that never
        // happens fails instead of hanging.
        await Assert.That(await scope.WaitForDisposalAsync()).IsEqualTo(1);
        await Assert.That(inner.Disposed).IsEqualTo(1);
    }

    [Test]
    public async Task Sync_dispose_then_async_dispose_releases_the_scope_once()
    {
        var scope = new ProbeScope();
        var inner = new ScriptedStream(Payload);
        var stream = new ScopeBoundStream(inner, scope);

        stream.Dispose();
        await stream.DisposeAsync();
        stream.Dispose();

        await Assert.That(await scope.WaitForDisposalAsync()).IsEqualTo(1);
        await Assert
            .That(inner.Disposed)
            .IsEqualTo(1)
            .Because("the producer must be disposed once too: a second dispose is a double-dispose of user code");
    }

    [Test]
    public async Task Async_dispose_after_eof_does_not_release_the_scope_twice()
    {
        var scope = new ProbeScope();
        var inner = new ScriptedStream(Payload);
        var stream = new ScopeBoundStream(inner, scope);

        await stream.ReadAsync(new byte[Payload.Length]);
        await stream.ReadAsync(new byte[Payload.Length]); // EOF: first release
        await stream.DisposeAsync(); // must be a second-path no-op, not a second release
        stream.Dispose();

        await Assert.That(scope.Disposals).IsEqualTo(1);
        await Assert.That(inner.Disposed).IsEqualTo(1);
    }

    [Test]
    public async Task Async_dispose_propagates_a_scope_release_failure_and_does_not_retry()
    {
        var scope = new ProbeScope { ThrowOnDispose = true };
        var stream = new ScopeBoundStream(new ScriptedStream(Payload), scope);

        await Assert
            .That(async () => await stream.DisposeAsync())
            .Throws<InvalidOperationException>()
            .Because("a failing scope release must reach the caller, not be swallowed");

        // The release was attempted and failed; repeating the dispose must not attempt a second one.
        await stream.DisposeAsync();
        await Assert.That(scope.Attempts).IsEqualTo(1);
    }

    private sealed class ScriptedStream(byte[] payload) : Stream
    {
        private int _position;

        public int Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default
        ) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed++;
            }

            base.Dispose(disposing);
        }

        private int Read(Span<byte> destination)
        {
            var remaining = payload.Length - _position;
            if (remaining == 0)
            {
                return 0;
            }

            var count = Math.Min(remaining, destination.Length);
            payload.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }
    }

    private sealed class ProbeScope : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposedOnce = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private int _disposals;

        public bool ThrowOnDispose { get; init; }

        public int Disposals => Volatile.Read(ref _disposals);

        public int Attempts { get; private set; }

        public Task<int> WaitForDisposalAsync() =>
            WaitAsync().ContinueWith(_ => Disposals, TaskScheduler.Default);

        public ValueTask DisposeAsync()
        {
            Attempts++;
            var count = Interlocked.Increment(ref _disposals);
            if (count == 1)
            {
                _disposedOnce.TrySetResult();
            }

            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("scope release failed");
            }

            return ValueTask.CompletedTask;
        }

        private async Task WaitAsync() =>
            await _disposedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
}
