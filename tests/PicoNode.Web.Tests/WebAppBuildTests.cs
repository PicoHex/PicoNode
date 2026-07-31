namespace PicoNode.Web.Tests;

internal sealed class TestContainer : ISvcContainer
{
    public ISvcContainer Register(SvcDescriptor descriptor) => this;

    public bool IsRegistered(Type serviceType) => false;

    public ISvcScope CreateScope() => new TestScope();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestScope : ISvcScope
{
    public object GetService(Type serviceType) => null!;

    public IReadOnlyList<object> GetServices(Type serviceType) => [];

    public bool TryGetService(Type serviceType, out object? result)
    {
        result = null;
        return false;
    }

    public bool TryGetServices(Type serviceType, out IReadOnlyList<object>? result)
    {
        result = null;
        return false;
    }

    public ISvcScope CreateScope() => new TestScope();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class WebAppBuildTests
{
    [Test]
    public async Task Constructor_requires_ISvcContainer()
    {
        var container = new TestContainer();
        var app = new WebApp(container);
        await Assert.That(app).IsNotNull();
    }

    [Test]
    public async Task Delegate_handler_is_wrapped_and_executed()
    {
        var app = new WebApp(new TestContainer());
        var invoked = false;
        app.MapGet(
            "/test",
            (WebContext ctx, CancellationToken _) =>
            {
                invoked = true;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
            }
        );

        var handler = app.Build();
        var context = new RecordingConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /test HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        await handler.OnReceivedAsync(context, request, CancellationToken.None);

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async Task Build_propagates_streaming_response_buffer_size_behaviorally()
    {
        var stream = new ChunkRecordingStream(Encoding.ASCII.GetBytes("abcdef"));
        var app = new WebApp(
            new TestContainer(),
            new WebAppOptions { StreamingResponseBufferSize = 3 }
        );
        app.MapGet(
            "/",
            (WebContext _, CancellationToken _) =>
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

        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public string? NegotiatedProtocol => null;

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
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

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

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Test]
    public async Task Multiple_exact_routes_all_match()
    {
        // Regression: verify multiple inline WebRequestHandler routes match.
        // This catches the case where /api/health works but /api/test doesn't.
        var app = new WebApp(new TestContainer());

        var healthCalled = false;
        var testCalled = false;

        app.MapGet(
            "/api/health",
            (WebContext ctx, CancellationToken _) =>
            {
                healthCalled = true;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
            }
        );

        app.MapGet(
            "/api/test",
            (WebContext ctx, CancellationToken _) =>
            {
                testCalled = true;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
            }
        );

        var handler = app.Build();

        // Send request to /api/health
        var bytes1 = Encoding.ASCII.GetBytes(
            "GET /api/health HTTP/1.1\r\nHost: example.com\r\n\r\n"
        );
        await handler.OnReceivedAsync(
            new RecordingConnectionContext(),
            new ReadOnlySequence<byte>(bytes1),
            CancellationToken.None
        );
        await Assert.That(healthCalled).IsTrue();

        // Send request to /api/test
        var bytes2 = Encoding.ASCII.GetBytes("GET /api/test HTTP/1.1\r\nHost: example.com\r\n\r\n");
        await handler.OnReceivedAsync(
            new RecordingConnectionContext(),
            new ReadOnlySequence<byte>(bytes2),
            CancellationToken.None
        );
        await Assert.That(testCalled).IsTrue();
    }

    [Test]
    public async Task Streaming_response_keeps_scope_alive_until_body_consumed()
    {
        var tracking = new TrackingScope();
        var pipe = new Pipe();

        var app = new WebApp(new TrackingScopeContainer(tracking));
        app.MapGet(
            "/stream",
            (WebContext ctx, CancellationToken ct) =>
            {
                // The body writer runs AFTER the pipeline returns. It reports whether
                // the DI scope was already disposed at that point. Pre-fix: "disposed=true"
                // (scope disposed when the pipeline returned); post-fix: "disposed=false".
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50, ct);
                    await pipe.Writer.WriteAsync(
                        Encoding.UTF8.GetBytes($"disposed={tracking.Disposed}"),
                        ct
                    );
                    await pipe.Writer.CompleteAsync();
                });
                return ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = pipe.Reader.AsStream(),
                    }
                );
            }
        );

        var handler = app.Build();
        var connection = new RecordingConnection();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /stream HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        await handler.OnReceivedAsync(connection, request, CancellationToken.None);

        var allText = string.Concat(connection.AllSent.Select(b => Encoding.ASCII.GetString(b)));
        await Assert
            .That(allText)
            .Contains("disposed=False")
            .Because("the DI scope must still be alive while the streaming body is being written");
    }

    private sealed class TrackingScope : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public void MarkDisposed() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>ISvcContainer/ISvcScope pair that tracks disposal and resolves TrackingScope.</summary>
    private sealed class TrackingScopeContainer(TrackingScope tracking) : ISvcContainer
    {
        public ISvcContainer Register(SvcDescriptor descriptor) => this;

        public bool IsRegistered(Type serviceType) => serviceType == typeof(TrackingScope);

        public ISvcScope CreateScope() => new TrackingSvcScope(tracking);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingSvcScope(TrackingScope tracking) : ISvcScope
    {
        public object GetService(Type serviceType) =>
            serviceType == typeof(TrackingScope) ? tracking : null!;

        public IReadOnlyList<object> GetServices(Type serviceType) =>
            serviceType == typeof(TrackingScope) ? [tracking] : [];

        public bool TryGetService(Type serviceType, out object? result)
        {
            result = serviceType == typeof(TrackingScope) ? tracking : null;
            return serviceType == typeof(TrackingScope);
        }

        public bool TryGetServices(Type serviceType, out IReadOnlyList<object>? result)
        {
            result = serviceType == typeof(TrackingScope) ? [tracking] : null;
            return serviceType == typeof(TrackingScope);
        }

        public ISvcScope CreateScope() => new TrackingSvcScope(tracking);

        public ValueTask DisposeAsync()
        {
            tracking.MarkDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConnection : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;

        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public string? NegotiatedProtocol => null;

        public byte[] LastSent { get; private set; } = [];

        public List<byte[]> AllSent { get; } = [];

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            LastSent = buffer.ToArray();
            AllSent.Add(LastSent);
            SendCount++;
            return Task.CompletedTask;
        }

        public void Close() => CloseCount++;
    }
}
