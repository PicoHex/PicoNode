namespace PicoAgent;

/// <summary>
/// Triggers PicoJetson source generation for types used in Agent HTTP/SSE responses.
/// </summary>
internal static class SerializerInit
{
    static SerializerInit()
    {
        // Named response types
        _ = PicoJetson.JsonSerializer.Serialize(new HealthResponse());
        _ = PicoJetson.JsonSerializer.Serialize(new ErrorResponse());
        _ = PicoJetson.JsonSerializer.Serialize(new OkResponse());

        // Collection types used in HTTP responses
        _ = PicoJetson.JsonSerializer.Serialize(new DiscoveredModel[0]);
        _ = PicoJetson.JsonSerializer.Serialize(new string[0]);
        _ = PicoJetson.JsonSerializer.Serialize(new Message[0]);

        // SSE event shapes (anonymous types auto-detected by shape: 2 string fields)
        // delta:  { type, content }   → 2-field
        // thinking:{ type, content }   → 2-field (same shape as delta)
        // done:    { type, stopReason }→ 2-field
        // error:   { type, message }   → 2-field
    }
}

internal sealed class HealthResponse
{
    public string Status { get; set; } = "ok";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
}

internal sealed class ErrorResponse
{
    public string Type { get; set; } = "error";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

internal sealed class OkResponse
{
    public string Status { get; set; } = "ok";
}
