using System.IO.Pipelines;
using PicoNode.Http;
using PicoNode.Http.Internal.ConnectionRuntime;
using PicoNode.Http.Internal.Hpack;

namespace PicoNode.Http.Tests;

/// <summary>
/// Response HEADERS must be HPACK-encoded exactly once per response: the
/// encoder's dynamic table is shared with the peer's decoder, so any entry
/// added by an encode whose bytes are never sent desynchronises every later
/// dynamic-table reference on the connection.
/// </summary>
public sealed class Http2ResponseEncodingTests
{
    [Test]
    public async Task Body_response_with_non_static_header_decodes_from_fresh_peer_table()
    {
        var decoded = await RunAsync(body: "data"u8.ToArray());

        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.Contains((":status", "200"))).IsTrue();
        await Assert.That(decoded.Contains(("x-custom", "v1"))).IsTrue();
    }

    [Test]
    public async Task Empty_body_response_with_non_static_header_decodes_from_fresh_peer_table()
    {
        var decoded = await RunAsync(body: null);

        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.Contains((":status", "200"))).IsTrue();
        await Assert.That(decoded.Contains(("x-custom", "v1"))).IsTrue();
    }

    private static async Task<List<(string, string)>?> RunAsync(byte[]? body)
    {
        var connection = new CaptureContext();
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            ReceivedPostPrefaceFrame = true,
        };

        HttpRequestHandler handler = (_, _) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Headers = [new KeyValuePair<string, string>("x-custom", "v1")],
                    Body = body ?? ReadOnlyMemory<byte>.Empty,
                }
            );

        var block = new List<byte>
        {
            0x82, // indexed :method GET (static index 2)
            0x04, // literal w/o indexing, name index 4 (:path)
            1,
            (byte)'/',
            0x86, // indexed :scheme http (static index 6)
        };
        var frame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            handler,
            null,
            CancellationToken.None
        );

        // Let the body pump run; the HEADERS frame is written synchronously.
        await Task.Delay(100);

        var headersFrame = connection
            .SentFrames.Select(ReadFrame)
            .FirstOrDefault(f => f is { Type: Http2FrameType.Headers, StreamId: 1 });
        if (headersFrame is null)
            return null;

        // A peer decodes with a table that has only ever seen the frames the
        // server actually sent — nothing else.
        var peerTable = new HpackDynamicTable();
        return HpackDecoder.TryDecode(
            headersFrame.Payload.Span,
            out var fields,
            peerTable,
            ConnectionRuntimeState.LocalHeaderTableSize
        )
            ? fields
            : null;
    }

    private static Http2Frame? ReadFrame(byte[] bytes)
    {
        if (bytes.Length < 9)
            return null;

        var length = (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
        if (bytes.Length < 9 + length)
            return null;

        var payload = new byte[length];
        Array.Copy(bytes, 9, payload, 0, length);

        return new Http2Frame
        {
            Type = (Http2FrameType)bytes[3],
            Flags = (Http2FrameFlags)bytes[4],
            StreamId = ((bytes[5] & 0x7F) << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8],
            Length = length,
            Payload = payload,
        };
    }

    private sealed class CaptureContext : ITcpConnectionContext
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _sent = [];

        public List<byte[]> SentFrames
        {
            get
            {
                lock (_gate)
                    return [.. _sent];
            }
        }

        public long ConnectionId => 1;
        public EndPoint RemoteEndPoint =>
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.MinValue;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.MinValue;
        public object? UserState { get; set; }
        public CancellationToken RemoteCloseToken => CancellationToken.None;
        public string? NegotiatedProtocol => null;

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default)
        {
            var bytes = new byte[buffer.Length];
            buffer.CopyTo(bytes);
            lock (_gate)
                _sent.Add(bytes);
            return Task.CompletedTask;
        }

        public void Close() { }
    }
}
