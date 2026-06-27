namespace PicoAgent;

public static class AgentEndpoints
{
    public static WebRequestHandler CreateMessageHandler(AgentHost host) =>
        async (context, ct) =>
        {
            var sessionId = context.RouteValues["id"] ?? "default";

            using var reader = new StreamReader(context.Request.BodyStream);
            var input = await reader.ReadToEndAsync(ct);

            var pipe = new Pipe();
            var sse = new SseConnection(pipe.Writer);

            _ = WriteAgentSseStreamAsync(sse, pipe.Writer, host, input, sessionId, ct);

            return new HttpResponse
            {
                StatusCode = 200,
                Headers =
                [
                    new("Content-Type", "text/event-stream"),
                    new("Cache-Control", "no-cache"),
                ],
                BodyStream = pipe.Reader.AsStream(),
            };
        };

    /// <summary>
    /// Creates a handler for POST /reload that rescans capabilities.
    /// </summary>
    public static WebRequestHandler CreateReloadHandler(
        CapabilityRegistry registry,
        string capabilitiesRoot
    ) => (_, _) =>
    {
        registry.Scan(capabilitiesRoot);
        var response = new HttpResponse
        {
            StatusCode = 200,
            Body = "{\"status\":\"reloaded\"}"u8.ToArray(),
        };
        return ValueTask.FromResult(response);
    };

    private static async Task WriteAgentSseStreamAsync(
        SseConnection sse,
        PipeWriter writer,
        AgentHost host,
        string input,
        string sessionId,
        CancellationToken ct
    )
    {
        try
        {
            await host.ProcessMessageAsync(
                input,
                ct,
                sessionId,
                onEvent: async (evt, ct2) =>
                {
                    switch (evt)
                    {
                        case AssistantMessageEvent.TextDelta td:
                            await sse.WriteJsonAsync(
                                PicoJetson.JsonSerializer.Serialize(new
                                {
                                    type = "delta",
                                    content = td.Delta,
                                }),
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.ThinkingDelta th:
                            await sse.WriteJsonAsync(
                                PicoJetson.JsonSerializer.Serialize(new
                                {
                                    type = "thinking",
                                    content = th.Delta,
                                }),
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.Done d:
                            await sse.WriteJsonAsync(
                                PicoJetson.JsonSerializer.Serialize(new
                                {
                                    type = "done",
                                    stopReason = d.Message.StopReason,
                                }),
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.Error e:
                            await sse.WriteJsonAsync(
                                PicoJetson.JsonSerializer.Serialize(new
                                {
                                    type = "error",
                                    message = e.Message.ErrorMessage,
                                }),
                                ct2
                            );
                            break;
                    }
                }
            );

            await sse.CompleteAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
        }
    }
}
