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
            PicoDocument? doc = null;
            try
            {
                doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
            }
            catch (Exception)
            {
                continue;
            } // skip malformed JSON lines

            // PicoDocument is not IDisposable
            var root = doc.RootElement;
            var type = root["type"].GetString();

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
                    var cbIndex = root["index"].GetInt32();
                    var cb = root["content_block"];
                    var cbType = cb["type"].GetString()!;

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
                            Id = cb["id"].GetString()!,
                            Name = cb["name"].GetString()!,
                        };
                        currentToolArgs.Clear();
                        yield return new AssistantMessageEvent.ToolCallStart
                        {
                            Index = cbIndex,
                            Name = cb["name"].GetString()!,
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    break;

                case TypeContentBlockDelta:
                    var deltaIndex = root["index"].GetInt32();
                    var delta = root["delta"];
                    var deltaType = delta["type"].GetString();

                    if (deltaType == DeltaTypeText)
                    {
                        var text = delta["text"].GetString()!;
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
                        var partialJson = delta["partial_json"].GetString()!;
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
                        var thinking = delta["thinking"].GetString()!;
                        yield return new AssistantMessageEvent.ThinkingDelta
                        {
                            Index = deltaIndex,
                            Delta = thinking,
                            Partial = ClonePartial(message, contentBlocks),
                        };
                    }
                    break;

                case TypeContentBlockStop:
                    var stopIdx = root["index"].GetInt32();
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
                            ? err["message"].GetString()
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
            return [];
        try
        {
            var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
            return PicoElementConverter.ObjectToDict(doc.RootElement);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
