namespace PicoNode.Tests;

public sealed class TcpConnectionBranchTests
{
    [Test]
    public async Task RunAsync_invokes_connected_received_and_closed_callbacks_on_happy_path()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler);
            var runTask = connection.RunAsync(handler);
            var payload = new byte[] { 1, 2, 3, 4 };

            var connected = await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(payload, SocketFlags.None);
            pair.Client.Shutdown(SocketShutdown.Send);

            var received = await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert
                .That(connected.RemoteEndPoint)
                .IsEqualTo((IPEndPoint)pair.Client.LocalEndPoint!);
            await Assert.That(received).IsEquivalentTo(payload);
            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.RemoteClosed);
            await Assert.That(closed.Error).IsNull();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_reports_handler_failed_and_closes_with_handler_fault_when_receive_handler_throws()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handlerException = new InvalidOperationException("handler boom");
            var handler = new RecordingTcpHandler(handlerException);
            var connection = CreateConnection(pair.Server, handler, logger);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(new byte[] { 42 }, SocketFlags.None);
            pair.Client.Shutdown(SocketShutdown.Send);

            await Task.Delay(500);
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(logger.Calls.Count).IsEqualTo(1);
            await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
            await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
            await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.HandlerFailed);
            await Assert.That(call.Exception).IsSameReferenceAs(handlerException);
            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.HandlerFailed);
            await Assert.That(closed.Error).IsSameReferenceAs(handlerException);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_closes_with_local_close_when_connection_is_closed_locally()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

            connection.Close();

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.LocalClose);
            await Assert.That(closed.Error).IsNull();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_closes_with_local_close_when_receive_loop_hits_object_disposed_after_close()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler);
            var runTask = connection.RunAsync(new ObjectDisposedTcpHandler(connection));

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.LocalClose);
            await Assert.That(closed.Error).IsNull();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_reports_receive_failed_and_closes_with_receive_fault_on_socket_exception()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler, logger);
            var exception = new SocketException((int)SocketError.NetworkDown);
            var runTask = connection.RunAsync(new SocketExceptionTcpHandler(exception, handler));

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(new byte[] { 9 }, SocketFlags.None);
            pair.Client.Shutdown(SocketShutdown.Send);

            await Task.Delay(500);
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(logger.Calls.Count).IsEqualTo(1);
            await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
            await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
            await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.ReceiveFailed);
            await Assert.That(call.Exception).IsSameReferenceAs(exception);
            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.ReceiveFailed);
            await Assert.That(closed.Error).IsSameReferenceAs(exception);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_returns_completed_task_for_empty_buffer()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);

            var task = connection.SendAsync(ReadOnlySequence<byte>.Empty);

            await Assert.That(task.IsCompletedSuccessfully).IsTrue();
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task DisposeAsync_can_be_called_multiple_times()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);

            await connection.DisposeAsync();
            await connection.DisposeAsync();

            await Assert
                .That(connection.SendAsync(new ReadOnlySequence<byte>(new byte[] { 1 })))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task NormalizeCompletionReason_returns_local_close_after_cancellation()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            await InvokeCancelConnectionAsync(connection);

            var result = InvokeNormalizeCompletionReason(connection, TcpCloseReason.RemoteClosed);

            await Assert.That(result).IsEqualTo(TcpCloseReason.LocalClose);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task ShutdownSocketSafely_swallows_socket_errors()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            await connection.DisposeAsync();

            InvokeShutdownSocketSafely(connection);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task InvokeConnectedAsync_awaits_asynchronous_handler_completion()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var handler = new AsyncConnectedTcpHandler();
            var context = GetConnectionContext(connection);

            var task = InvokeConnectedAsync(connection, handler, context, CancellationToken.None);

            await Assert.That(task.IsCompleted).IsFalse();

            handler.Release();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task InvokeClosedHandlerAsync_reports_fault_when_async_handler_throws()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var exception = new InvalidOperationException("close handler boom");
            var handler = new AsyncThrowingClosedTcpHandler(exception);
            var connection = CreateConnection(pair.Server, handler, logger);

            var task = InvokeClosedHandlerAsync(
                connection,
                handler,
                TcpCloseReason.LocalClose,
                null
            );

            await Assert.That(task.IsCompleted).IsFalse();

            handler.Release();

            await task.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(logger.Calls.Count).IsEqualTo(1);
            await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
            await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
            await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.HandlerFailed);
            await Assert.That(call.Exception).IsSameReferenceAs(exception);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_uses_scatter_gather_for_multi_segment_array_backed_sequence()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var segments = new[]
            {
                new byte[] { 1, 2 },
                new byte[] { 3 },
                new byte[] { 4, 5, 6 },
                new byte[] { 7, 8 },
            };
            var expected = segments.SelectMany(static x => x).ToArray();

            await connection.SendAsync(CreateSequence(segments));

            var received = await ReceiveExactAsync(pair.Client, expected.Length);

            await Assert.That(received).IsEquivalentTo(expected);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_copies_when_multi_segment_sequence_exceeds_scatter_gather_limit()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var segments = Enumerable.Range(1, 17).Select(static x => new[] { (byte)x }).ToArray();
            var expected = segments.SelectMany(static x => x).ToArray();

            await connection.SendAsync(CreateSequence(segments));

            var received = await ReceiveExactAsync(pair.Client, expected.Length);

            await Assert.That(received).IsEquivalentTo(expected);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_ignores_empty_segments_in_multi_segment_sequences()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var expected = new byte[] { 1, 2, 3 };

            await connection.SendAsync(
                CreateSequence(
                    Array.Empty<byte>(),
                    new byte[] { 1, 2 },
                    Array.Empty<byte>(),
                    new byte[] { 3 }
                )
            );

            var received = await ReceiveExactAsync(pair.Client, expected.Length);

            await Assert.That(received).IsEquivalentTo(expected);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_copies_non_array_backed_multi_segment_sequences()
    {
        var pair = await CreateConnectedSocketsAsync();
        using var owner1 = MemoryPool<byte>.Shared.Rent(2);
        using var owner2 = MemoryPool<byte>.Shared.Rent(3);

        owner1.Memory.Span[0] = 9;
        owner1.Memory.Span[1] = 8;
        owner2.Memory.Span[0] = 7;
        owner2.Memory.Span[1] = 6;
        owner2.Memory.Span[2] = 5;

        try
        {
            var connection = CreateConnection(pair.Server);
            var expected = new byte[] { 9, 8, 7, 6, 5 };

            await connection.SendAsync(CreateSequence(owner1.Memory[..2], owner2.Memory[..3]));

            var received = await ReceiveExactAsync(pair.Client, expected.Length);

            await Assert.That(received).IsEquivalentTo(expected);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task AdvanceSentSegments_skips_empty_segments_and_trims_partially_sent_segment()
    {
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5 };
        var segments = new[]
        {
            ArraySegment<byte>.Empty,
            new ArraySegment<byte>(first),
            new ArraySegment<byte>(second),
        };

        var nextIndex = InvokeAdvanceSentSegments(segments, 0, 4);

        await Assert.That(nextIndex).IsEqualTo(2);
        await Assert.That(segments[0].Count).IsEqualTo(0);
        await Assert.That(segments[1].Count).IsEqualTo(0);
        await Assert.That(segments[2].Array).IsSameReferenceAs(second);
        await Assert.That(segments[2].Offset).IsEqualTo(1);
        await Assert.That(segments[2].Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryBuildBufferList_returns_empty_segments_for_empty_sequence()
    {
        var result = InvokeTryBuildBufferList(ReadOnlySequence<byte>.Empty, out var segments);

        await Assert.That(result).IsTrue();
        await Assert.That(segments.Length).IsEqualTo(0);
    }

    [Test]
    public async Task TryBuildBufferList_returns_false_for_non_array_backed_memory()
    {
        using var owner = new NonArrayMemoryManager(new byte[] { 1, 2, 3 });
        var sequence = CreateSequence(owner.Memory);

        var result = InvokeTryBuildBufferList(sequence, out var segments);

        await Assert.That(result).IsFalse();
        await Assert.That(segments.Length).IsEqualTo(0);
    }

    [Test]
    public async Task PumpSocketToPipeAsync_returns_remote_closed_when_connection_is_already_cancelled()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var cts = GetCtsToken(connection);
            await InvokeCancelConnectionAsync(connection);

            var reason = await InvokePumpSocketToPipeAsync(connection, cts);

            await Assert.That(reason).IsEqualTo(TcpCloseReason.RemoteClosed);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task PumpSocketToPipeAsync_returns_remote_closed_after_completed_flush()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var pipe = GetPipe(connection);

            pipe.Reader.Complete();
            await pair.Client.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);

            var reason = await InvokePumpSocketToPipeAsync(connection, CancellationToken.None);

            await Assert.That(reason).IsEqualTo(TcpCloseReason.RemoteClosed);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task MapSocketExceptionReason_returns_remote_closed_for_connection_reset()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var result = InvokeMapSocketExceptionReason(
                connection,
                new SocketException((int)SocketError.ConnectionReset),
                TcpCloseReason.RemoteClosed
            );

            await Assert.That(result).IsEqualTo(TcpCloseReason.RemoteClosed);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task MapSocketExceptionReason_returns_local_close_while_a_local_close_is_in_progress()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);

            // A reset-class error seen while a local close is under way is that close's own
            // shutdown/dispose landing on the in-flight receive. The fact is the close state,
            // not the connection token: the receive loop's ordered teardown cancels that token
            // on every escape, so a token-based test would call every peer reset a local close.
            connection.Close();

            var result = InvokeMapSocketExceptionReason(
                connection,
                new SocketException((int)SocketError.OperationAborted),
                TcpCloseReason.RemoteClosed
            );

            await Assert.That(result).IsEqualTo(TcpCloseReason.LocalClose);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task MapSocketExceptionReason_returns_receive_fault_for_other_socket_errors()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var result = InvokeMapSocketExceptionReason(
                connection,
                new SocketException((int)SocketError.NetworkDown),
                TcpCloseReason.RemoteClosed
            );

            await Assert.That(result).IsEqualTo(TcpCloseReason.ReceiveFailed);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task MapSocketExceptionReason_preserves_local_close_for_other_socket_errors()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            var result = InvokeMapSocketExceptionReason(
                connection,
                new SocketException((int)SocketError.NetworkDown),
                TcpCloseReason.LocalClose
            );

            await Assert.That(result).IsEqualTo(TcpCloseReason.LocalClose);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task ShouldReportReceiveFault_returns_true_for_remote_closed_and_receive_fault()
    {
        await Assert.That(InvokeShouldReportReceiveFault(TcpCloseReason.RemoteClosed)).IsTrue();
        await Assert.That(InvokeShouldReportReceiveFault(TcpCloseReason.ReceiveFailed)).IsTrue();
        await Assert.That(InvokeShouldReportReceiveFault(TcpCloseReason.LocalClose)).IsFalse();
    }

    [Test]
    public async Task SendAsync_with_uncancellable_token_does_not_hang_when_connection_is_disposed()
    {
        // The caller passed CancellationToken.None, so a send queued on _sendLock
        // cannot observe connection cancellation. Disposing the connection used to
        // dispose the semaphore while that send was still queued: SemaphoreSlim.Dispose
        // does not release pending waiters, and the in-flight send's Release() then
        // threw ObjectDisposedException — stranding the queued send forever.
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var stream = new BlockingStream();
            var connection = CreateConnection(pair.Server, stream: stream);

            var first = connection.SendAsync(
                new ReadOnlySequence<byte>(new byte[] { 1 }),
                CancellationToken.None
            );
            await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(3));

            var second = connection.SendAsync(
                new ReadOnlySequence<byte>(new byte[] { 2 }),
                CancellationToken.None
            );

            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

            // Unblock the in-flight write so its finally can release the lock.
            stream.Release();

            try
            {
                await second.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException) { }
            catch (Exception)
            { /* the send may legitimately fail — it just must not hang */
            }

            await Assert
                .That(second.IsCompleted)
                .IsTrue()
                .Because("a queued send must not be stranded on a disposed semaphore");

            try
            {
                await first.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception) { }
        }
        finally
        {
            pair.Client.Dispose();
        }
    }

    [Test]
    public async Task CloseAsync_during_handler_does_not_overlap_OnClosedAsync()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new ParkedReceiveTcpHandler();
            var connection = CreateConnection(pair.Server, handler);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(new byte[] { 7 }, SocketFlags.None);
            await handler.ReceivedStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // The handler sits inside OnReceivedAsync until ReleaseHandler() is called and only
            // this test can release it, so nothing in the close sequence can legitimately finish
            // while it is parked. A close that tears the pipe and the socket down without waiting
            // for the handler loop runs OnClosedAsync right here instead.
            connection.Close();

            // Bounded expectation, not synchronisation: the violation is unblocked and therefore
            // surfaces within microseconds, while the correct behaviour cannot surface at all
            // before the release below.
            var closedWhileHandlerParked = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromMilliseconds(500)
            );

            handler.ReleaseHandler();
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.LocalClose);
            await Assert
                .That(closedWhileHandlerParked)
                .IsFalse()
                .Because(
                    "OnClosedAsync must not run while the handler loop is still inside OnReceivedAsync"
                );
            await Assert
                .That(handler.CloseHandlerStartedAfterHandlerReturned)
                .IsTrue()
                .Because("OnClosedAsync must observe the handler loop as finished");

            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task WriteStarted => _writeStarted.Task;

        public void Release() => _release.TrySetResult();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            _writeStarted.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            _writeStarted.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }

        public override ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            Release();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Stands in for an <c>SslStream</c>: it delivers one byte (so the handler gets a request and
    /// parks inside <c>OnReceivedAsync</c>) and then fails the next read with <paramref name="fault"/>
    /// — by default a plain <see cref="IOException"/>, the shape a TLS/stream framing fault arrives
    /// in, which is neither a <see cref="SocketException"/> nor a cancellation.
    /// </summary>
    private sealed class FaultingReadStream(Task faultAfter, Exception? fault = null) : Stream
    {
        private readonly Task _faultAfter = faultAfter;
        private readonly Exception _fault = fault ?? new IOException("TLS read fault");
        private readonly TaskCompletionSource _faulted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _reads;

        /// <summary>Completes when the read fault has been raised.</summary>
        public Task Faulted => _faulted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                buffer.Span[0] = 0x42;
                return 1;
            }

            // The first read exists only to put the handler inside OnReceivedAsync, so the
            // fault must not land before it got there: waiting for that seam is what makes
            // the overlap the test looks for deterministic instead of a matter of scheduling.
            await _faultAfter.WaitAsync(cancellationToken).ConfigureAwait(false);
            _faulted.TrySetResult();
            throw _fault;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Test]
    public async Task CloseAsync_completes_the_run_task()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // The connection must keep the task of its handler loop instead of dropping it:
            // that task is what tells a close whether the handler loop is still running.
            await Assert
                .That(connection.RunTask)
                .IsSameReferenceAs(runTask)
                .Because("the handler loop task must be reachable from the connection");

            connection.Close();

            // Close() is fire-and-forget, so the only way to know the loop is finished is
            // that its task completes — and it must, or a close could never wait for it.
            await connection.RunTask!.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(connection.RunTask.IsCompleted).IsTrue();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_with_stream_read_fault_closes_only_after_the_handler_loop()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new ParkedReceiveTcpHandler();
            var stream = new FaultingReadStream(handler.ReceivedStarted.Task);
            var connection = CreateConnection(pair.Server, handler, stream: stream);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await handler.ReceivedStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // A read fault (a TLS/SSL framing error arrives as a plain IOException) is neither
            // a SocketException nor a cancellation, and it used to escape the receive loop with
            // the handler still parked in OnReceivedAsync and the pipe writer never completed.
            // The close sequence then ran on its "the handler loop already returned" assumption:
            // OnClosedAsync overlapped the live handler, and the orphaned loop later failed
            // unobserved on the completed reader.
            await stream.Faulted.WaitAsync(TimeSpan.FromSeconds(3));

            // Bounded expectation, not synchronisation: the violation is unblocked and shows up
            // immediately, while the correct behaviour is blocked on a release only this test
            // controls and therefore cannot complete inside the window.
            var closedWhileHandlerParked = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromMilliseconds(500)
            );

            handler.ReleaseHandler();
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.ReceiveFailed);
            await Assert.That(closed.Error).IsNotNull().Because("the read fault is reported");
            await Assert
                .That(closedWhileHandlerParked)
                .IsFalse()
                .Because(
                    "a read fault must not run OnClosedAsync while the handler loop is still inside OnReceivedAsync"
                );
            await Assert
                .That(handler.CloseHandlerStartedAfterHandlerReturned)
                .IsTrue()
                .Because("a read fault must close through the ordered path, not around it");

            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_with_stream_read_fault_releases_a_handler_on_the_connection_token()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new TokenParkedTcpHandler();
            var stream = new FaultingReadStream(handler.ReceivedStarted);
            var connection = CreateConnection(pair.Server, handler, stream: stream);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // A streaming handler parks on the connection token, not on a pipe read. Nothing in
            // this test releases it: only the ordered teardown can, by firing that token. A read
            // fault that skipped that first step would leave the handler waiting for a
            // cancellation that the close sequence cannot deliver until the handler returns —
            // the deadlock the reset branch has always avoided, now owed to every escape.
            await stream.Faulted.WaitAsync(TimeSpan.FromSeconds(3));

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.ReceiveFailed);
            await Assert
                .That(handler.CancellationObserved)
                .IsTrue()
                .Because(
                    "a faulted read must release a handler that waits on the connection token"
                );

            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_with_a_throwing_remote_close_registration_keeps_the_ordered_teardown()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new ParkedReceiveTcpHandler();
            // The reset path: here the loop's OWN teardown is what fires the remote-close token,
            // and it fires it as one step of a sequence whose remaining steps — completing the
            // writer and awaiting the handler loop — are what keep OnClosedAsync off a live
            // handler. A fault escaping at the fire replaces the fault that brought us here, so
            // the loop's caller relabels a peer RST as a handler fault and runs the close with
            // `waitsForHandlerLoop: false` over a handler that is still parked.
            var reset = new SocketException((int)SocketError.ConnectionReset);
            var readFault = new IOException("TLS read fault", reset);
            var letFault = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var stream = new FaultingReadStream(letFault.Task, readFault);
            var connection = CreateConnection(pair.Server, handler, logger, stream);
            var runTask = connection.RunAsync(handler);

            await handler.ReceivedStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // A user registration on the token the transport hands out — the shape of a
            // streaming response that fails while it is being cancelled. The first registration
            // is the test's own signal that the loop reached its fire; the second one throws and
            // is what the fix has to tolerate.
            var fired = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var signal = connection.RemoteCloseToken.Register(() => fired.TrySetResult());
            var callbackFault = new InvalidOperationException("token callback boom");
            using var registration = connection.RemoteCloseToken.Register(() =>
            {
                throw callbackFault;
            });

            letFault.TrySetResult();
            await stream.Faulted.WaitAsync(TimeSpan.FromSeconds(3));
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // Bounded expectation, not synchronisation: a teardown that stops at its second step
            // is unblocked and closes right here, while the correct one is parked on the handler
            // loop and cannot close inside the window.
            var closedWhileHandlerParked = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromMilliseconds(500)
            );

            handler.ReleaseHandler();
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert
                .That(closedWhileHandlerParked)
                .IsFalse()
                .Because(
                    "a throwing token callback must not skip the writer completion and the wait for the handler loop"
                );
            await Assert
                .That(handler.CloseHandlerStartedAfterHandlerReturned)
                .IsTrue()
                .Because(
                    "the loop's teardown must still await the handler loop before it rethrows"
                );
            await Assert
                .That(closed.Reason)
                .IsEqualTo(TcpCloseReason.RemoteClosed)
                .Because("the peer's reset is the reason, not the callback's collateral");
            await Assert.That(closed.Error).IsSameReferenceAs(readFault);
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.HandlerFailed
                        && Mentions(x.Exception, callbackFault)
                    )
                )
                .IsTrue()
                .Because("the callback's fault is reported instead of being swallowed");
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.ReceiveFailed
                        && ReferenceEquals(x.Exception, readFault)
                    )
                )
                .IsTrue()
                .Because(
                    "the read fault is still the one reported, not replaced by the callback's"
                );

            // The processing task can only finish on a COMPLETED writer, so a run task that ends
            // here is the proof that step 3 of the teardown ran (step 4 is the wait itself).
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_with_a_throwing_remote_close_registration_survives_a_graceful_fin()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new ParkedReceiveTcpHandler();
            var connection = CreateConnection(pair.Server, handler, logger);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(new byte[] { 9 }, SocketFlags.None);
            await handler.ReceivedStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            // The same throwing registration, met by the OTHER site the loop fires this token: the
            // graceful FIN branch, where the fire is the first await after a pump that returned
            // normally. (The reset branch is pinned by the test above; this one makes a fix that
            // only guards one of the two sites fail.)
            var fired = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var signal = connection.RemoteCloseToken.Register(() => fired.TrySetResult());
            var callbackFault = new InvalidOperationException("token callback boom");
            using var registration = connection.RemoteCloseToken.Register(() =>
            {
                throw callbackFault;
            });

            pair.Client.Shutdown(SocketShutdown.Send);
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var closedWhileHandlerParked = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromMilliseconds(500)
            );

            handler.ReleaseHandler();
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert
                .That(closedWhileHandlerParked)
                .IsFalse()
                .Because(
                    "a throwing token callback on the FIN branch must not skip the writer completion and the handler wait"
                );
            await Assert
                .That(handler.CloseHandlerStartedAfterHandlerReturned)
                .IsTrue()
                .Because("the FIN branch must still await the handler loop before it returns");
            await Assert
                .That(closed.Reason)
                .IsEqualTo(TcpCloseReason.RemoteClosed)
                .Because("a graceful FIN stays a remote close, whatever a callback did");
            await Assert.That(closed.Error).IsNull();
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.HandlerFailed
                        && Mentions(x.Exception, callbackFault)
                    )
                )
                .IsTrue()
                .Because("the callback's fault is reported instead of being swallowed");

            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RemoteCloseToken_fires_on_local_close()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler);

            // A local close of a connection whose receive loop has not started yet: the shape a
            // node produces when it closes a connection it has tracked but not yet run, and when
            // it disposes one it refused to track. Today nothing on this path touches the
            // remote-close source — the close sequence never fires it, and there is no receive
            // loop whose teardown could do it by accident — so a response that waits on this
            // token waits for ever.
            connection.Close();

            await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert
                .That(connection.RemoteCloseToken.IsCancellationRequested)
                .IsTrue()
                .Because("the close path itself fires RemoteCloseToken, not the receive loop");
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RemoteCloseToken_fires_on_node_stop()
    {
        var handler = new ParkedConnectTcpHandler();
        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
            }
        );
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint!);
        var token = await handler.RemoteCloseToken.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // The handler is parked inside OnConnectedAsync on a source only this test can
        // release, so the receive loop cannot reach its own teardown — the close path is the
        // only thing left that can deliver the signal. That is the reason the fire lives in
        // CloseCoreAsync and not (only) in the receive loop: there may be no loop, or the loop
        // may not be past OnConnectedAsync when the connection is closed.
        await handler.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => fired.TrySetResult());

        var stopTask = node.StopAsync();
        try
        {
            // A node stop is a LOCAL close: the peer sends no FIN and no RST. A response that
            // outlives its request (SSE) waits on this token, so a stop that leaves it
            // un-cancelled leaves that response running until some unrelated backstop reaps it.
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            handler.ReleaseConnect();
            try
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            { // the stop still has to finish; the assertion below owns the verdict
            }
        }

        await Assert
            .That(token.IsCancellationRequested)
            .IsTrue()
            .Because("a node stop fires RemoteCloseToken for every connection it closes");
    }

    [Test]
    public async Task RemoteCloseToken_is_readable_after_dispose()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler);
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            connection.Close();
            await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Handler code reads the token after the connection is gone (a response task that
            // is still finishing, a keep-alive loop shutting down). The property is the only
            // way a handler can reach it, so it must stay readable once the source is disposed.
            void ReadToken() => _ = connection.RemoteCloseToken.IsCancellationRequested;

            await Assert
                .That(ReadToken)
                .ThrowsNothing()
                .Because(
                    "the token struct is cached at construction, so disposing its source cannot make the property throw"
                );
            await Assert.That(connection.RemoteCloseToken.IsCancellationRequested).IsTrue();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task Close_with_a_throwing_remote_close_registration_still_tears_the_connection_down()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler, logger);

            // A user registration on the token the transport hands out — the shape of a
            // streaming response that fails while it is being cancelled. CancelAsync collects
            // it and completes the cancellation task faulted, so firing the token is a step
            // that CAN throw. It must not be able to abandon the rest of the teardown: the
            // socket shutdown, the handler wait, OnClosedAsync and the pipe/socket dispose all
            // follow it in the same sequence, and a close that stops at its own first statement
            // leaks the socket and pipe and strands the node's drain on the connection it never
            // finished closing.
            //
            // No receive loop is started here on purpose: the loop's teardown can also fire
            // this source, and the point of the test is what the CLOSE PATH does with a
            // registration that throws.
            var callbackFault = new InvalidOperationException("token callback boom");
            using var registration = connection.RemoteCloseToken.Register(() =>
            {
                throw callbackFault;
            });

            connection.Close();

            // Both windows are deliberately many times the handler-loop bound (half the default
            // 5 s DrainTimeout, i.e. 2.5 s): a bounded wait for the handler loop is a legitimate
            // step of this close, so a window equal to the bound would turn a healthy close into a
            // false failure as soon as this test grows a variant whose handler parks.
            var closedInTime = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromSeconds(10)
            );
            // The close is fire-and-forget, so the dispose is observed by a bounded probe rather
            // than by a sleep that would impose an order. The socket is disposed after the pipe
            // ends are completed, in one synchronous block, so seeing it disposed means both.
            var tornDown = await PollUntilAsync(
                () => IsSocketDisposed(pair.Server),
                TimeSpan.FromSeconds(10)
            );

            await Assert
                .That(closedInTime)
                .IsTrue()
                .Because("a throwing token callback must not skip OnClosedAsync");
            await Assert
                .That(tornDown)
                .IsTrue()
                .Because("a throwing token callback must not skip the pipe/socket dispose");
            await Assert
                .That(handler.Closed.Task.IsCompletedSuccessfully)
                .IsTrue()
                .Because("the close handler ran to completion");
            await Assert
                .That(handler.Closed.Task.Result.Reason)
                .IsEqualTo(TcpCloseReason.LocalClose);
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.HandlerFailed
                        && Mentions(x.Exception, callbackFault)
                    )
                )
                .IsTrue()
                .Because(
                    "the registration's fault is reported against tcp.close.core instead of being swallowed"
                );
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task Close_with_a_throwing_connection_token_registration_still_tears_the_connection_down()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(pair.Server, handler, logger);

            // A user registration on the CONNECTION token — the shape of a request handler that
            // fails while its token fires. Firing it is step 2 of the close sequence, and the
            // socket shutdown, the wait for the handler loop, OnClosedAsync and the pipe/socket
            // dispose all follow it, so a fault escaping here used to abandon them and strand the
            // node's drain on a connection nobody finished closing.
            var callbackFault = new InvalidOperationException("connection token boom");
            using var registration = GetConnectionTokenSource(connection)
                .Token.Register(() =>
                {
                    throw callbackFault;
                });

            connection.Close();

            var closedInTime = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromSeconds(10)
            );
            var tornDown = await PollUntilAsync(
                () => IsSocketDisposed(pair.Server),
                TimeSpan.FromSeconds(10)
            );

            await Assert
                .That(closedInTime)
                .IsTrue()
                .Because("a throwing token callback must not skip OnClosedAsync");
            await Assert
                .That(tornDown)
                .IsTrue()
                .Because("a throwing token callback must not skip the pipe/socket dispose");
            await Assert.That(handler.Closed.Task.IsCompletedSuccessfully).IsTrue();
            await Assert
                .That(handler.Closed.Task.Result.Reason)
                .IsEqualTo(TcpCloseReason.LocalClose);
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.HandlerFailed
                        && Mentions(x.Exception, callbackFault)
                    )
                )
                .IsTrue()
                .Because(
                    "the callback's fault is reported against tcp.close.core instead of escaping"
                );
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task Reset_with_a_throwing_connection_token_registration_still_runs_the_teardown()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new RecordingTcpHandler();
            var reset = new SocketException((int)SocketError.ConnectionReset);
            var readFault = new IOException("TLS read fault", reset);
            var stream = new FaultingReadStream(handler.Received.Task, readFault);
            var connection = CreateConnection(pair.Server, handler, logger, stream);

            // The same registration, reached through the OTHER site that fires the connection
            // token: the ordered teardown in the receive loop. Step 1 of four — the remote-close
            // fire, the writer completion and the wait for the handler loop follow it — so a
            // callback that throws there must be reported against that site and stepped over, not
            // allowed to replace the peer reset with its own collateral.
            var callbackFault = new InvalidOperationException("connection token boom");
            using var registration = GetConnectionTokenSource(connection)
                .Token.Register(() =>
                {
                    throw callbackFault;
                });

            var runTask = connection.RunAsync(handler);

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert
                .That(closed.Reason)
                .IsEqualTo(TcpCloseReason.RemoteClosed)
                .Because("the peer's reset is still the reason, not the callback's fault");
            await Assert.That(closed.Error).IsSameReferenceAs(readFault);
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.HandlerFailed
                        && (
                            x.Message?.Contains("tcp.close.teardown", StringComparison.Ordinal)
                            ?? false
                        )
                        && Mentions(x.Exception, callbackFault)
                    )
                )
                .IsTrue()
                .Because("the throwing callback is reported against the teardown that fired it");
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.ReceiveFailed
                        && ReferenceEquals(x.Exception, readFault)
                    )
                )
                .IsTrue()
                .Because("the original fault is still reported, not replaced");
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_with_io_exception_wrapping_a_reset_closes_as_remote_closed()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new RecordingTcpHandler();
            var reset = new SocketException((int)SocketError.ConnectionReset);
            var readFault = new IOException("TLS read fault", reset);
            var stream = new FaultingReadStream(handler.Received.Task, readFault);
            var connection = CreateConnection(pair.Server, handler, logger, stream);
            var runTask = connection.RunAsync(handler);

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            // The reset is the PEER's close (RST), which TcpCloseReason names, and the mapping
            // must say so for a TLS/stream read fault exactly as it does for a bare socket
            // reset. The connection token is not a proxy for "we closed locally": the ordered
            // teardown cancels it on the way out of the pump, so a mapping that asks the token
            // answers "yes, the teardown ran" and files every reset as a silent local close.
            await Assert
                .That(closed.Reason)
                .IsEqualTo(TcpCloseReason.RemoteClosed)
                .Because("an RST from the peer is a remote close, not a local one");
            await Assert.That(closed.Error).IsSameReferenceAs(readFault);
            await Assert
                .That(logger.Calls.Count)
                .IsEqualTo(1)
                .Because("a peer reset is reported once, against the receive that saw it");
            await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
            await Assert.That(call!.EventId.Id).IsEqualTo((int)NodeFaultCode.ReceiveFailed);
            await Assert.That(call.Exception).IsSameReferenceAs(readFault);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task RunAsync_with_stream_read_fault_bounds_the_wait_for_a_handler_that_never_returns()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new NeverReturningTcpHandler();
            var stream = new FaultingReadStream(handler.ReceivedStarted.Task);
            var connection = CreateConnection(pair.Server, handler, logger, stream);
            var runTask = connection.RunAsync(handler);

            await stream.Faulted.WaitAsync(TimeSpan.FromSeconds(3));

            try
            {
                // The handler ignores the completed pipe writer and both cancellation tokens,
                // so nothing but a bound can end the wait the receive loop does before it lets
                // the close sequence run. Without one the close never happens at all — and with
                // it the node's drain, which waits for exactly that close.
                var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15));

                await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.ReceiveFailed);
                await Assert.That(closed.Error).IsNotNull();
                await Assert
                    .That(
                        logger.Calls.Any(x =>
                            x.EventId.Id == (int)NodeFaultCode.StopFailed
                            && x.Exception is TimeoutException
                        )
                    )
                    .IsTrue()
                    .Because("the bound gives up loudly instead of blocking for ever");
            }
            finally
            {
                handler.ReleaseHandler();
            }

            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            { /* the abandoned handler loop is expected to fault once it is released */
            }
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    /// <summary>
    /// Bounded observation of "this task completed", used to assert the ABSENCE of an event:
    /// a violation is unblocked and shows up immediately, whereas the correct behaviour is
    /// blocked on a seam the test owns and therefore cannot complete inside the window.
    /// </summary>
    private static async Task<bool> HasCompletedWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bounded observation of a side effect that has no task to await (disposal). Same contract
    /// as <see cref="HasCompletedWithinAsync"/>: the delay is only the poll interval on a
    /// positive observation, never an order the test relies on.
    /// </summary>
    /// <remarks>
    /// The deadline is on a <see cref="Stopwatch"/>: a wall clock can step (NTP, DST) either way
    /// inside a bounded window, and a window that is asserted on must not be able to expire early.
    /// </remarks>
    private static async Task<bool> PollUntilAsync(Func<bool> probe, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (probe())
            {
                return true;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// True once the socket has been disposed. A shutdown on a live socket succeeds or fails
    /// with <see cref="SocketException"/> (already shut down); only a disposed one throws
    /// <see cref="ObjectDisposedException"/>, so the probe never confuses the two.
    /// </summary>
    private static bool IsSocketDisposed(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/> is, wraps or aggregates <paramref name="target"/>:
    /// a cancellation callback's fault reaches the fault report either directly or inside the
    /// <see cref="AggregateException"/> the cancellation task faults with.
    /// </summary>
    private static bool Mentions(Exception? exception, Exception target)
    {
        if (exception is null)
        {
            return false;
        }

        if (ReferenceEquals(exception, target))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(x => Mentions(x, target));
        }

        return Mentions(exception.InnerException, target);
    }

    private static TcpConnection CreateConnection(
        Socket serverSocket,
        ITcpConnectionHandler? handler = null,
        ILogger? logger = null,
        Stream? stream = null
    )
    {
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = (IPEndPoint)serverSocket.LocalEndPoint!,
                ConnectionHandler = handler ?? new NoOpTcpHandler(),
                Logger = logger,
            }
        );

        return new TcpConnection(node, serverSocket, stream);
    }

    /// <summary>
    /// The connection's own token source — the one the close path and the receive-loop teardown
    /// both fire. Internal by design, so a test that needs a registration on it has to reach for
    /// it.
    /// </summary>
    private static CancellationTokenSource GetConnectionTokenSource(TcpConnection connection) =>
        (CancellationTokenSource)
            typeof(TcpConnectionLifecycle)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(GetLifecycle(connection))!;

    private static object GetLifecycle(TcpConnection connection)
    {
        var field = typeof(TcpConnection).GetField(
            "_lifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return field.GetValue(connection)!;
    }

    private static object GetReceiveLoop(TcpConnection connection)
    {
        var field = typeof(TcpConnection).GetField(
            "_receiveLoop",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return field.GetValue(connection)!;
    }

    private static TcpCloseReason InvokeMapSocketExceptionReason(
        TcpConnection connection,
        SocketException exception,
        TcpCloseReason currentReason
    )
    {
        var lifecycle = GetLifecycle(connection);
        var method = typeof(TcpConnectionLifecycle).GetMethod(
            "MapSocketExceptionReason",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (TcpCloseReason)method.Invoke(lifecycle, [exception, currentReason])!;
    }

    private static bool InvokeShouldReportReceiveFault(TcpCloseReason reason)
    {
        var method = typeof(TcpConnectionLifecycle).GetMethod(
            "ShouldReportReceiveFault",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        return (bool)method.Invoke(null, [reason])!;
    }

    private static TcpCloseReason InvokeNormalizeCompletionReason(
        TcpConnection connection,
        TcpCloseReason reason
    )
    {
        var method = typeof(TcpConnectionReceiveLoop).GetMethod(
            "NormalizeCompletionReason",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var cts = (CancellationTokenSource)
            typeof(TcpConnection)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(connection)!;
        return (TcpCloseReason)method.Invoke(null, [reason, cts.Token])!;
    }

    private static void InvokeShutdownSocketSafely(TcpConnection connection)
    {
        var lifecycle = GetLifecycle(connection);
        var method = typeof(TcpConnectionLifecycle).GetMethod(
            "ShutdownSocketSafely",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        method.Invoke(lifecycle, [null]);
    }

    private static Task InvokeConnectedAsync(
        TcpConnection connection,
        ITcpConnectionHandler handler,
        ITcpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var method = typeof(TcpConnectionReceiveLoop).GetMethod(
            "InvokeConnectedAsync",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        return (Task)method.Invoke(null, [handler, context, cancellationToken])!;
    }

    private static Task InvokeClosedHandlerAsync(
        TcpConnection connection,
        ITcpConnectionHandler handler,
        TcpCloseReason reason,
        Exception? error
    )
    {
        var lifecycle = GetLifecycle(connection);
        var method = typeof(TcpConnectionLifecycle).GetMethod(
            "InvokeClosedHandlerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (Task)method.Invoke(lifecycle, [handler, reason, error])!;
    }

    private static ITcpConnectionContext GetConnectionContext(TcpConnection connection)
    {
        var lifecycle = GetLifecycle(connection);
        var field = typeof(TcpConnectionLifecycle).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (ITcpConnectionContext)field.GetValue(lifecycle)!;
    }

    private static int InvokeAdvanceSentSegments(
        ArraySegment<byte>[] segments,
        int startIndex,
        int bytesSent
    )
    {
        var method = typeof(SendPath).GetMethod(
            "AdvanceSentSegments",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        return (int)method.Invoke(null, [segments, startIndex, bytesSent])!;
    }

    private static bool InvokeTryBuildBufferList(
        ReadOnlySequence<byte> buffer,
        out ArraySegment<byte>[] segments
    )
    {
        var method = typeof(SendPath).GetMethod(
            "TryBuildBufferList",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var arguments = new object?[] { buffer, null };
        var result = (bool)method.Invoke(null, arguments)!;
        segments = (ArraySegment<byte>[])arguments[1]!;
        return result;
    }

    private static async Task<TcpCloseReason> InvokePumpSocketToPipeAsync(
        TcpConnection connection,
        CancellationToken cancellationToken
    )
    {
        var receiveLoop = GetReceiveLoop(connection);
        var method = typeof(TcpConnectionReceiveLoop).GetMethod(
            "PumpSocketToPipeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return await (Task<TcpCloseReason>)method.Invoke(receiveLoop, [cancellationToken])!;
    }

    private static Pipe GetPipe(TcpConnection connection)
    {
        var pipeField = typeof(TcpConnection).GetField(
            "_pipe",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (Pipe)pipeField.GetValue(connection)!;
    }

    private static async Task InvokeCancelConnectionAsync(TcpConnection connection)
    {
        var lifecycle = GetLifecycle(connection);
        var method = typeof(TcpConnectionLifecycle).GetMethod(
            "CancelConnectionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        await (Task)method.Invoke(lifecycle, [])!;
    }

    private static CancellationToken GetCtsToken(TcpConnection connection)
    {
        var lifecycle = GetLifecycle(connection);
        var field = typeof(TcpConnectionLifecycle).GetField(
            "_cts",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var cts = (CancellationTokenSource)field.GetValue(lifecycle)!;
        return cts.Token;
    }

    private static async Task<(Socket Client, Socket Server)> CreateConnectedSocketsAsync()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        var server = await listener.AcceptAsync();
        await connectTask;
        listener.Dispose();
        return (client, server);
    }

    private static async Task<byte[]> ReceiveExactAsync(Socket socket, int length)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        while (totalRead < length)
        {
            var read = await socket.ReceiveAsync(
                buffer.AsMemory(totalRead, length - totalRead),
                SocketFlags.None,
                cts.Token
            );
            if (read <= 0)
            {
                throw new InvalidOperationException(
                    "Socket closed before all bytes were received."
                );
            }

            totalRead += read;
        }

        return buffer;
    }

    private static ReadOnlySequence<byte> CreateSequence(params byte[][] segments)
    {
        var memories = segments.Select(static segment => (ReadOnlyMemory<byte>)segment).ToArray();
        return CreateSequence(memories);
    }

    private static ReadOnlySequence<byte> CreateSequence(params ReadOnlyMemory<byte>[] segments)
    {
        BufferSegment? first = null;
        BufferSegment? last = null;

        foreach (var segment in segments)
        {
            var current = new BufferSegment(segment);
            if (first is null)
            {
                first = current;
                last = current;
                continue;
            }

            last = last!.Append(current);
        }

        return first is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> buffer)
        {
            Memory = buffer;
        }

        public BufferSegment Append(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            return next;
        }
    }

    private sealed class NonArrayMemoryManager(byte[] buffer) : MemoryManager<byte>
    {
        private byte[]? _buffer = buffer;

        public override Span<byte> GetSpan() => _buffer;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            _buffer = null;
        }
    }

    private sealed class AsyncConnectedTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => _release.Task.WaitAsync(cancellationToken);

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class ObjectDisposedTcpHandler(TcpConnection connection) : ITcpConnectionHandler
    {
        private readonly TcpConnection _connection = connection;

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            _connection.Close();
            return Task.CompletedTask;
        }

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => throw new ObjectDisposedException(nameof(TcpConnection));

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }

    private sealed class SocketExceptionTcpHandler(
        SocketException exception,
        RecordingTcpHandler closedRecorder
    ) : ITcpConnectionHandler
    {
        private readonly SocketException _exception = exception;
        private readonly RecordingTcpHandler _closedRecorder = closedRecorder;

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => _closedRecorder.OnConnectedAsync(connection, cancellationToken);

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromException<SequencePosition>(_exception);

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => _closedRecorder.OnClosedAsync(connection, reason, error, cancellationToken);
    }

    private sealed class AsyncThrowingClosedTcpHandler(Exception exception) : ITcpConnectionHandler
    {
        private readonly Exception _exception = exception;
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public async ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            await _release.Task.WaitAsync(cancellationToken);
            throw _exception;
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    /// <summary>
    /// Handler whose OnReceivedAsync parks until the test releases it, recording the order in
    /// which the handler loop finished and OnClosedAsync started. A close that tears the pipe
    /// down without waiting for the handler loop shows up as a close-handler sequence number
    /// that precedes the handler's own.
    /// </summary>
    /// <summary>
    /// Handler that parks inside <c>OnReceivedAsync</c> on the connection token itself — the
    /// shape a streaming handler takes. Only a cancellation of that token releases it, so a
    /// teardown that never fires the token waits for it for ever.
    /// </summary>
    private sealed class TokenParkedTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _receivedStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _cancellationObserved;

        public TaskCompletionSource<ConnectedTcpConnection> Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClosedTcpConnection> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReceivedStarted => _receivedStarted.Task;

        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult(
                new ConnectedTcpConnection(connection.ConnectionId, connection.RemoteEndPoint)
            );
            return Task.CompletedTask;
        }

        public async ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            _receivedStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected: the close fires the connection token to release a streaming handler
            }

            Volatile.Write(ref _cancellationObserved, 1);
            return buffer.End;
        }

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Captures the transport's <see cref="ITcpConnectionContext.RemoteCloseToken"/> at connect
    /// time — the way a handler that outlives its request (SSE) does — and then parks inside
    /// <c>OnConnectedAsync</c> on a source only the test can release. The receive loop therefore
    /// cannot reach its own teardown while the handler is parked, so the token's fate across a
    /// close is decided by the close path alone.
    /// </summary>
    private sealed class ParkedConnectTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<CancellationToken> RemoteCloseToken { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            RemoteCloseToken.TrySetResult(connection.RemoteCloseToken);
            ConnectStarted.TrySetResult();
            await _released.Task.ConfigureAwait(false);
        }

        public void ReleaseConnect() => _released.TrySetResult();

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handler that parks inside <c>OnReceivedAsync</c> on a source only the test can release:
    /// it ignores the completed pipe writer and both cancellation tokens, so it is the shape a
    /// close sequence can neither wait out nor release.
    /// </summary>
    private sealed class NeverReturningTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<ConnectedTcpConnection> Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReceivedStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClosedTcpConnection> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult(
                new ConnectedTcpConnection(connection.ConnectionId, connection.RemoteEndPoint)
            );
            return Task.CompletedTask;
        }

        public async ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            ReceivedStarted.TrySetResult();
            await _released.Task.ConfigureAwait(false);
            return buffer.End;
        }

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return ValueTask.CompletedTask;
        }

        public void ReleaseHandler() => _released.TrySetResult();
    }

    private sealed class ParkedReceiveTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _handlerReturned;

        public TaskCompletionSource<ConnectedTcpConnection> Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReceivedStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClosedTcpConnection> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Sampled when OnClosedAsync starts: false means the close handler ran while
        /// OnReceivedAsync was still parked, which is the overlap the fix rules out.
        /// </summary>
        public bool CloseHandlerStartedAfterHandlerReturned { get; private set; }

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult(
                new ConnectedTcpConnection(connection.ConnectionId, connection.RemoteEndPoint)
            );
            return Task.CompletedTask;
        }

        public async ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            ReceivedStarted.TrySetResult();
            await _released.Task.ConfigureAwait(false);
            Volatile.Write(ref _handlerReturned, 1);
            return buffer.End;
        }

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            CloseHandlerStartedAfterHandlerReturned = Volatile.Read(ref _handlerReturned) != 0;
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return ValueTask.CompletedTask;
        }

        public void ReleaseHandler() => _released.TrySetResult();
    }

    private sealed class RecordingTcpHandler(Exception? receiveException = null)
        : ITcpConnectionHandler
    {
        private readonly Exception? _receiveException = receiveException;

        public TaskCompletionSource<ConnectedTcpConnection> Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<byte[]> Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClosedTcpConnection> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult(
                new ConnectedTcpConnection(connection.ConnectionId, connection.RemoteEndPoint)
            );
            return Task.CompletedTask;
        }

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            Received.TrySetResult(buffer.ToArray());

            return _receiveException is null
                ? ValueTask.FromResult(buffer.End)
                : ValueTask.FromException<SequencePosition>(_receiveException);
        }

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpTcpHandler : ITcpConnectionHandler
    {
        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }

    private sealed record ConnectedTcpConnection(long ConnectionId, EndPoint RemoteEndPoint);

    private sealed record ClosedTcpConnection(TcpCloseReason Reason, Exception? Error);
}
