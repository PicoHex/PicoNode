using System.IO.Pipelines;

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
            var faults = new ConcurrentQueue<NodeFault>();
            var faultReported = new TaskCompletionSource<NodeFault>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var handlerException = new InvalidOperationException("handler boom");
            var handler = new RecordingTcpHandler(handlerException);
            var connection = CreateConnection(
                pair.Server,
                handler,
                fault =>
                {
                    faults.Enqueue(fault);
                    if (fault.Code == NodeFaultCode.HandlerFailed)
                    {
                        faultReported.TrySetResult(fault);
                    }
                }
            );
            var runTask = connection.RunAsync(handler);

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(new byte[] { 42 }, SocketFlags.None);
            pair.Client.Shutdown(SocketShutdown.Send);

            var fault = await faultReported.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(faults.Count).IsEqualTo(1);
            await Assert.That(fault.Code).IsEqualTo(NodeFaultCode.HandlerFailed);
            await Assert.That(fault.Operation).IsEqualTo("tcp.handler");
            await Assert.That(fault.Exception).IsSameReferenceAs(handlerException);
            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.HandlerFault);
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
            var faults = new ConcurrentQueue<NodeFault>();
            var faultReported = new TaskCompletionSource<NodeFault>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var handler = new RecordingTcpHandler();
            var connection = CreateConnection(
                pair.Server,
                handler,
                fault =>
                {
                    faults.Enqueue(fault);
                    if (fault.Code == NodeFaultCode.ReceiveFailed)
                    {
                        faultReported.TrySetResult(fault);
                    }
                }
            );
            var exception = new SocketException((int)SocketError.NetworkDown);
            var runTask = connection.RunAsync(new SocketExceptionTcpHandler(exception, handler));

            await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Client.SendAsync(new byte[] { 9 }, SocketFlags.None);
            pair.Client.Shutdown(SocketShutdown.Send);

            var fault = await faultReported.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(faults.Count).IsEqualTo(1);
            await Assert.That(fault.Code).IsEqualTo(NodeFaultCode.ReceiveFailed);
            await Assert.That(fault.Operation).IsEqualTo("tcp.receive");
            await Assert.That(fault.Exception).IsSameReferenceAs(exception);
            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.ReceiveFault);
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
            var faults = new ConcurrentQueue<NodeFault>();
            var faultReported = new TaskCompletionSource<NodeFault>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var exception = new InvalidOperationException("close handler boom");
            var handler = new AsyncThrowingClosedTcpHandler(exception);
            var connection = CreateConnection(
                pair.Server,
                handler,
                fault =>
                {
                    faults.Enqueue(fault);
                    if (fault.Operation == "tcp.close.handler")
                    {
                        faultReported.TrySetResult(fault);
                    }
                }
            );

            var task = InvokeClosedHandlerAsync(
                connection,
                handler,
                TcpCloseReason.LocalClose,
                null
            );

            await Assert.That(task.IsCompleted).IsFalse();

            handler.Release();

            var fault = await faultReported.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await task.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(faults.Count).IsEqualTo(1);
            await Assert.That(fault.Code).IsEqualTo(NodeFaultCode.HandlerFailed);
            await Assert.That(fault.Operation).IsEqualTo("tcp.close.handler");
            await Assert.That(fault.Exception).IsSameReferenceAs(exception);
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
            await InvokeCancelConnectionAsync(connection);

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
    public async Task MapSocketExceptionReason_returns_local_close_after_connection_cancellation()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var connection = CreateConnection(pair.Server);
            await InvokeCancelConnectionAsync(connection);

            var result = InvokeMapSocketExceptionReason(
                connection,
                new SocketException((int)SocketError.OperationAborted),
                TcpCloseReason.RemoteClosed
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

            await Assert.That(result).IsEqualTo(TcpCloseReason.ReceiveFault);
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
        await Assert.That(InvokeShouldReportReceiveFault(TcpCloseReason.ReceiveFault)).IsTrue();
        await Assert.That(InvokeShouldReportReceiveFault(TcpCloseReason.LocalClose)).IsFalse();
    }

    private static TcpConnection CreateConnection(
        Socket serverSocket,
        ITcpConnectionHandler? handler = null,
        Action<NodeFault>? faultHandler = null
    )
    {
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = (IPEndPoint)serverSocket.LocalEndPoint!,
                ConnectionHandler = handler ?? new NoOpTcpHandler(),
                FaultHandler = faultHandler,
            }
        );

        return new TcpConnection(node, serverSocket);
    }

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
        method.Invoke(lifecycle, []);
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
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => _release.Task.WaitAsync(cancellationToken);

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

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

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
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

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => _closedRecorder.OnClosedAsync(connection, reason, error, cancellationToken);
    }

    private sealed class AsyncThrowingClosedTcpHandler(Exception exception) : ITcpConnectionHandler
    {
        private readonly Exception _exception = exception;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public async Task OnClosedAsync(
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

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return Task.CompletedTask;
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

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed record ConnectedTcpConnection(long ConnectionId, IPEndPoint RemoteEndPoint);

    private sealed record ClosedTcpConnection(TcpCloseReason Reason, Exception? Error);
}
