namespace PicoAgent.Web;

internal static class SerializerInit
{
    static SerializerInit()
    {
        _ = PicoJetson.JsonSerializer.Serialize(new SseDeltaEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseThinkingEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseDoneEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new SseErrorEvent());
        _ = PicoJetson.JsonSerializer.Serialize(new DiscoveredModel());
        _ = PicoJetson.JsonSerializer.Serialize(new Message());
    }
}
