namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class WebSocketMessageProcessorState
{
    public WebSocketOpCode? MessageOpCode { get; set; }

    public ArrayBufferWriter<byte> PayloadBuffer { get; } = new();
}
