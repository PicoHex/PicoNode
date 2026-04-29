namespace PicoNode;

internal sealed class SendPath(Socket socket, Stream? stream)
{
    private const int MaxScatterGatherSegments = 16;

    public async Task SendSequenceAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        if (stream is not null)
        {
            await SendViaStreamAsync(buffer, cancellationToken);
            return;
        }

        if (buffer.IsSingleSegment)
        {
            await SendMemoryAsync(socket, buffer.First, cancellationToken);
            return;
        }

        if (TryBuildBufferList(buffer, out var segments))
        {
            await SendBufferListAsync(socket, segments, cancellationToken);
            return;
        }

        await CopyAndSendAsync(socket, buffer, cancellationToken);
    }

    private async Task SendViaStreamAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        if (buffer.IsSingleSegment)
        {
            await stream!.WriteAsync(buffer.First, cancellationToken);
        }
        else
        {
            var tempBuffer = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
            try
            {
                buffer.CopyTo(tempBuffer);
                await stream!.WriteAsync(
                    tempBuffer.AsMemory(0, (int)buffer.Length),
                    cancellationToken
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tempBuffer, clearArray: false);
            }
        }

        await stream!.FlushAsync(cancellationToken);
    }

    private static async Task CopyAndSendAsync(
        Socket socket,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var tempBuffer = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
        try
        {
            buffer.CopyTo(tempBuffer);
            await SendMemoryAsync(
                socket,
                tempBuffer.AsMemory(0, (int)buffer.Length),
                cancellationToken
            );
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempBuffer, clearArray: false);
        }
    }

    private static async Task SendMemoryAsync(
        Socket socket,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        while (!buffer.IsEmpty)
        {
            var sendOperation = socket.SendAsync(buffer, SocketFlags.None, cancellationToken);
            var bytesSent = sendOperation.IsCompletedSuccessfully
                ? sendOperation.Result
                : await sendOperation;
            if (bytesSent <= 0)
            {
                throw new SocketException((int)SocketError.ConnectionAborted);
            }

            buffer = buffer[bytesSent..];
        }
    }

    private static async Task SendBufferListAsync(
        Socket socket,
        ArraySegment<byte>[] segments,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var remainingOffset = 0;
            while (remainingOffset < segments.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sendOperation = socket.SendAsync(segments, SocketFlags.None);
                var bytesSent = sendOperation.IsCompletedSuccessfully
                    ? sendOperation.Result
                    : await sendOperation.WaitAsync(cancellationToken);
                if (bytesSent <= 0)
                {
                    throw new SocketException((int)SocketError.ConnectionAborted);
                }

                remainingOffset = AdvanceSentSegments(segments, remainingOffset, bytesSent);
            }
        }
        finally
        {
            Array.Clear(segments, 0, segments.Length);
        }
    }

    private static int AdvanceSentSegments(
        ArraySegment<byte>[] segments,
        int startIndex,
        int bytesSent
    )
    {
        while (startIndex < segments.Length)
        {
            var segment = segments[startIndex];
            if (segment.Count == 0)
            {
                startIndex++;
                continue;
            }

            if (bytesSent < segment.Count)
            {
                segments[startIndex] = new ArraySegment<byte>(
                    segment.Array!,
                    segment.Offset + bytesSent,
                    segment.Count - bytesSent
                );
                return startIndex;
            }

            bytesSent -= segment.Count;
            segments[startIndex] = default;
            startIndex++;
        }

        return startIndex;
    }

    private static bool TryBuildBufferList(
        ReadOnlySequence<byte> buffer,
        out ArraySegment<byte>[] segments
    )
    {
        var segmentCount = 0;
        foreach (var memory in buffer)
        {
            if (memory.IsEmpty)
            {
                continue;
            }

            if (!MemoryMarshal.TryGetArray(memory, out _))
            {
                segments =  [];
                return false;
            }

            segmentCount++;
            if (segmentCount <= MaxScatterGatherSegments)
                continue;
            segments =  [];
            return false;
        }

        if (segmentCount == 0)
        {
            segments =  [];
            return true;
        }

        segments = new ArraySegment<byte>[segmentCount];
        var index = 0;
        foreach (var memory in buffer)
        {
            if (memory.IsEmpty)
            {
                continue;
            }

            MemoryMarshal.TryGetArray(memory, out segments[index]);
            index++;
        }

        return true;
    }
}
