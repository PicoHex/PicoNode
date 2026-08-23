namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
{
    private static async ValueTask<bool> ProcessWebSocketOverHttp2(
        ITcpConnectionContext connection,
        Http2Frame frame,
        Http2StreamState state,
        List<KeyValuePair<string, string>> regularHeaders,
        Dictionary<string, string> headerDict,
        CancellationToken ct
    )
    {
        // Send 200 response to complete the extended CONNECT handshake
        var responseHeaders = new List<(string, string)> { (":status", "200") };

        var headerWriter = new ArrayBufferWriter<byte>();
        EncodeResponseHeadersHpack(connection, responseHeaders, headerWriter);
        await WriteHeadersFrameAsync(
            connection,
            state.StreamId,
            Http2FrameFlags.EndHeaders,
            headerWriter.WrittenMemory,
            ct
        );

        // The tunnel is established. Subsequent DATA frames on this stream
        // carry WebSocket frames. For the MVP, we echo received data on the
        // same stream as an opaque tunnel. Full WebSocket frame encoding
        // would require bridging WebSocketFrameCodec with HTTP/2 DATA frames.
        state.ResponseSent = true;
        return false;
    }
}
