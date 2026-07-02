
namespace PicoNode.AI;

public static class SseParser
{
    // Anthropic SSE protocol constants
    private const string EventPrefix = "event: ";
    private const string DataPrefix = "data: ";
    private const string EventMessageStop = "message_stop";
    private const string TypeMessageStart = "message_start";
    private const string TypeContentBlockStart = "content_block_start";
    private const string TypeContentBlockDelta = "content_block_delta";
    private const string TypeContentBlockStop = "content_block_stop";
    private const string TypeMessageDelta = "message_delta";
    private const string TypeError = "error";
    private const string BlockTypeText = "text";
    private const string BlockTypeToolUse = "tool_use";
    private const string DeltaTypeText = "text_delta";
    private const string DeltaTypeInputJson = "input_json_delta";
    private const string DeltaTypeThinking = "thinking_delta";

    public static async IAsyncEnumerable<AssistantMessageEvent> ParseAnthropicStreamAsync(
        Stream stream,
        string model,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var contentBlocks = new List<ContentBlock>();
        var currentToolArgs = new StringBuilder();
        var message = new Message
        {
            Role = "assistant",
            Model = model,
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
        };

        bool started = false;
        string? stopReason = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                break;

            // SSE: lines starting with "data: " contain JSON
            if (!line.StartsWith(DataPrefix))
            {
                if (line.StartsWith(EventPrefix) && line[EventPrefix.Length..] == EventMessageStop)
                {
                    message.ContentBlocks = contentBlocks.ToArray();
                    message.StopReason = stopReason ?? "end_turn";
                    yield return new AssistantMessageEvent.Done { Message = message };
                    yield break;
                }
                continue;
            }

            var json = line[DataPrefix.Length..]; // skip "data: "
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            } // skip malformed JSON lines

            using var _doc = doc;
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case TypeMessageStart:
                    if (!started)
                    {
                        started = true;
                        yield return new AssistantMessageEvent.Start { Partial = message };
                    }
                    break;

                case TypeContentBlockStart:
                    var cbIndex = root.GetProperty("index").GetInt32();
                    var cb = root.GetProperty("content_block");
                    var cbType = cb.GetProperty("type").GetString()!;

                    if (cbType == BlockTypeText)
                    {
                        while (contentBlocks.Count <= cbIndex)
                            contentBlocks.Add(new ContentBlock());
                        contentBlocks[cbIndex] = new ContentBlock { Type = "text", Text = "" };
                    }
                    else if (cbType == BlockTypeToolUse)
                    {
                        while (contentBlocks.Count <= cbIndex)
                            contentBlocks.Add(new ContentBlock());
                        contentBlocks[cbIndex] = new ContentBlock
                        {
                            Type = "tool_call",
                            Id = cb.GetProperty("id").GetString()!,
                            Name = cb.GetProperty("name").GetString()!,
                        };
                        currentToolArgs.Clear();
                        yield return new AssistantMessageEvent.ToolCallStart
                        {
                            Index = cbIndex,
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    break;

                case TypeContentBlockDelta:
                    var deltaIndex = root.GetProperty("index").GetInt32();
                    var delta = root.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();

                    if (deltaType == DeltaTypeText)
                    {
                        var text = delta.GetProperty("text").GetString()!;
                        if (contentBlocks[deltaIndex].Type == "text")
                        {
                            contentBlocks[deltaIndex].Text += text;
                        }
                        yield return new AssistantMessageEvent.TextDelta
                        {
                            Index = deltaIndex,
                            Delta = text,
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    else if (deltaType == DeltaTypeInputJson)
                    {
                        var partialJson = delta.GetProperty("partial_json").GetString()!;
                        currentToolArgs.Append(partialJson);
                        yield return new AssistantMessageEvent.ToolCallDelta
                        {
                            Index = deltaIndex,
                            Delta = partialJson,
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    else if (deltaType == DeltaTypeThinking)
                    {
                        var thinking = delta.GetProperty("thinking").GetString()!;
                        yield return new AssistantMessageEvent.ThinkingDelta
                        {
                            Index = deltaIndex,
                            Delta = thinking,
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    break;

                case TypeContentBlockStop:
                    var stopIdx = root.GetProperty("index").GetInt32();
                    if (contentBlocks[stopIdx].Type == "tool_call")
                    {
                        contentBlocks[stopIdx].Arguments = ParseJsonObject(
                            currentToolArgs.ToString()
                        );
                        yield return new AssistantMessageEvent.ToolCallEnd
                        {
                            Index = stopIdx,
                            Call = contentBlocks[stopIdx],
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    break;

                case TypeMessageDelta:
                    if (
                        root.TryGetProperty("delta", out var msgDelta)
                        && msgDelta.TryGetProperty("stop_reason", out var sr)
                    )
                    {
                        stopReason = sr.GetString();
                    }
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("output_tokens", out var ot))
                            message.Usage = new TokenUsage { OutputTokens = ot.GetInt32() };
                    }
                    break;

                case TypeError:
                    var errorMsg = new Message
                    {
                        Role = "assistant",
                        ErrorMessage = root.TryGetProperty("error", out var err)
                            ? err.GetProperty("message").GetString()
                            : "Unknown error",
                        StopReason = "error",
                    };
                    yield return new AssistantMessageEvent.Error { Message = errorMsg };
                    yield break;
            }
        }
    }

    private static Message ClonePartial(Message msg, List<ContentBlock> blocks)
    {
        return new Message
        {
            Role = msg.Role,
            Model = msg.Model,
            Provider = msg.Provider,
            Api = msg.Api,
            ContentBlocks = blocks.ToArray(),
        };
    }

    private static Dictionary<string, object?> ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new();
        using var doc = JsonDocument.Parse(json);
        var result = ParseElement(doc.RootElement);
        return (result as Dictionary<string, object?>) ?? new();
    }

    private static object? ParseElement(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => ParseElement(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(ParseElement).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
