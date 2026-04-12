using System.Buffers;
using System.Net;

namespace PicoNode.Web.Tests;

public sealed class WebAppBuildTests
{
    [Test]
    public async Task Build_propagates_streaming_response_buffer_size_behaviorally()
    {
        var stream = new ChunkRecordingStream(Encoding.ASCII.GetBytes("abcdef"));
        var app = new WebApp(new WebAppOptions { StreamingResponseBufferSize = 3, });
        app.MapGet(
            "/",
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = stream,
                    }
                )
        );

        var handler = app.Build();
        var context = new RecordingConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        await Assert.That(stream.ReadBufferSizes.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(stream.ReadBufferSizes.All(static size => size == 3)).IsTrue();
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;

        public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public void Close() { }
    }

    private sealed class ChunkRecordingStream(byte[] buffer) : Stream
    {
        private readonly byte[] _buffer = buffer;
        private int _position;

        public List<int> ReadBufferSizes { get; } = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default
        )
        {
            ReadBufferSizes.Add(destination.Length);

            if (_position >= _buffer.Length)
            {
                return ValueTask.FromResult(0);
            }

            var bytesToRead = Math.Min(2, _buffer.Length - _position);
            _buffer.AsMemory(_position, bytesToRead).CopyTo(destination);
            _position += bytesToRead;
            return ValueTask.FromResult(bytesToRead);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
