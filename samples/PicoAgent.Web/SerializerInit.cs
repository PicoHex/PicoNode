namespace PicoAgent.Web;

internal static class SerializerInit
{
    static SerializerInit()
    {
        _ = PicoJetson.JsonSerializer.Serialize(new SseDeltaEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseThinkingEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseDoneEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseErrorEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseToolCallStartEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseToolCallDeltaEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseToolCallEndEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new DiscoveredModel());
        _ = PicoJetson.JsonSerializer.Serialize(new Message());
    }
}
