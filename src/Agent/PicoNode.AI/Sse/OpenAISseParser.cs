namespace PicoNode.AI;

public static class OpenAISseParser
{
    private const string DataPrefix = "data: ";
    private const string DoneMarker = "[DONE]";
    private const string JsonPropChoices = "choices";
    private const string JsonPropDelta = "delta";
    private const string JsonPropContent = "content";
    private const string JsonPropReasoningContent = "reasoning_content";
    private const string JsonPropFinishReason = "finish_reason";
    private const string JsonPropToolCalls = "tool_calls";
    private const string JsonPropFunction = "function";
    private const string JsonPropArguments = "arguments";

    public static async IAsyncEnumerable<AssistantMessageEvent> ParseStreamAsync(
        Stream stream,
        string model,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var contentAccum = new StringBuilder();
        var contentBlocks = new List<ContentBlock>();
        var reasoningAccum = new StringBuilder();
        var message = new Message
        {
            Role = "assistant",
            Model = model,
            Provider = "openai",
            Api = AiApiFormat.OpenAIChatCompletions,
        };

        bool started = false;
        bool sawContent = false;
        // Track tool calls by index: index → (id, name, args accumulator)
        var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                break;

            if (!line.StartsWith(DataPrefix))
                continue;

            var json = line[DataPrefix.Length..];

            if (json.Trim() == DoneMarker)
            {
                FlushToolCalls(contentBlocks, toolCalls);
                if (contentAccum.Length > 0)
                    contentBlocks.Add(
                        new ContentBlock { Type = "text", Text = contentAccum.ToString() }
                    );
                message.ContentBlocks = contentBlocks.ToArray();
                message.StopReason = "stop";
                message.ReasoningContent = reasoningAccum.GetContent();
                yield return new AssistantMessageEvent.Done { Message = message };
                yield break;
            }

            PicoDocument? doc = null;
            try
            {
                doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
            }
            catch (Exception)
            {
                continue;
            }

            var root = doc.RootElement;

            if (
                !root.TryGetProperty(JsonPropChoices, out var choices)
                || choices.GetArrayLength() == 0
            )
                continue;

            var choice = choices[0];
            if (!choice.TryGetProperty(JsonPropDelta, out var delta))
                continue;

            // ── Tool calls (OpenAI function calling) ──
            if (delta.TryGetProperty(JsonPropToolCalls, out var tcArray))
            {
                for (int i = 0; i < tcArray.GetArrayLength(); i++)
                {
                    var tc = tcArray[i];
                    var index = tc.TryGetProperty("index", out var idxProp)
                        ? idxProp.GetInt32()
                        : 0;

                    if (!toolCalls.TryGetValue(index, out var tcState))
                    {
                        // First chunk — create new tool call
                        var id = tc.TryGetProperty("id", out var idProp)
                            ? idProp.GetString() ?? ""
                            : "";
                        string name = "";
                        if (
                            tc.TryGetProperty(JsonPropFunction, out var func)
                            && func.TryGetProperty("name", out var nameProp)
                        )
                            name = nameProp.GetString() ?? "";
                        tcState = (id, name, new StringBuilder());
                        toolCalls[index] = tcState;

                        yield return new AssistantMessageEvent.ToolCallStart
                        {
                            Index = index,
                            Name = name,
                            Partial = new Message
                            {
                                Role = "assistant",
                                ContentBlocks =
                                [
                                    new ContentBlock
                                    {
                                        Type = "tool_call",
                                        Id = id,
                                        Name = name,
                                    },
                                ],
                            },
                        };
                    }

                    // Accumulate arguments
                    if (
                        tc.TryGetProperty(JsonPropFunction, out var tcFunc)
                        && tcFunc.TryGetProperty(JsonPropArguments, out var argsProp)
                    )
                    {
                        var argsFrag = argsProp.GetString() ?? "";
                        tcState.Args.Append(argsFrag);
                        toolCalls[index] = tcState;

                        yield return new AssistantMessageEvent.ToolCallDelta
                        {
                            Index = index,
                            Delta = argsFrag,
                            Partial = new Message
                            {
                                Role = "assistant",
                                ContentBlocks =
                                [
                                    new ContentBlock
                                    {
                                        Type = "tool_call",
                                        Id = tcState.Id,
                                        Name = tcState.Name,
                                    },
                                ],
                            },
                        };
                    }
                }
            }

            // ── Reasoning content ──
            if (delta.TryGetProperty(JsonPropReasoningContent, out var reasoning))
            {
                var text = reasoning.GetStringOrNull();
                if (text is not null)
                {
                    reasoningAccum.Append(text);
                    yield return new AssistantMessageEvent.ThinkingDelta
                    {
                        Index = 0,
                        Delta = text,
                    };
                    if (!sawContent)
                    {
                        contentAccum.Append(text);
                        if (!started)
                        {
                            started = true;
                            yield return new AssistantMessageEvent.Start { Partial = message };
                        }
                        yield return new AssistantMessageEvent.TextDelta
                        {
                            Index = 0,
                            Delta = text,
                            Partial = new Message
                            {
                                Role = "assistant",
                                Model = model,
                                ContentBlocks =
                                [
                                    new ContentBlock
                                    {
                                        Type = "text",
                                        Text = contentAccum.ToString(),
                                    },
                                ],
                            },
                        };
                    }
                }
            }

            // ── Text content ──
            if (delta.TryGetProperty(JsonPropContent, out var contentVal))
            {
                var text = contentVal.GetStringOrNull() ?? "";
                sawContent = true;
                contentAccum.Append(text);

                if (!started)
                {
                    started = true;
                    yield return new AssistantMessageEvent.Start { Partial = message };
                }

                yield return new AssistantMessageEvent.TextDelta
                {
                    Index = 0,
                    Delta = text,
                    Partial = new Message
                    {
                        Role = "assistant",
                        Model = model,
                        ContentBlocks =
                        [
                            new ContentBlock { Type = "text", Text = contentAccum.ToString() },
                        ],
                    },
                };
            }

            // ── Finish reason ──
            if (
                choice.TryGetProperty(JsonPropFinishReason, out var fr)
                && fr.GetStringOrNull() is { } reason
                && reason != "null"
                && reason != ""
            )
            {
                // Emit ToolCallEnd for all accumulated tool calls
                foreach (var (idx, state) in toolCalls)
                {
                    var parsed = ParseToolArgs(state.Args.ToString());
                    contentBlocks.Add(
                        new ContentBlock
                        {
                            Type = "tool_call",
                            Id = state.Id,
                            Name = state.Name,
                            Arguments = parsed,
                        }
                    );
                    yield return new AssistantMessageEvent.ToolCallEnd
                    {
                        Index = idx,
                        Call = new ContentBlock
                        {
                            Type = "tool_call",
                            Id = state.Id,
                            Name = state.Name,
                            Arguments = parsed,
                        },
                        Partial = new Message { Role = "assistant" },
                    };
                }
                toolCalls.Clear();

                if (contentAccum.Length > 0)
                    contentBlocks.Add(
                        new ContentBlock { Type = "text", Text = contentAccum.ToString() }
                    );

                message.ContentBlocks = contentBlocks.ToArray();
                message.StopReason = reason;
                // Prefer choice.message.reasoning_content over delta-accumulated
                // (DeepSeek may send full reasoning in message, partial in delta)
                message.ReasoningContent =
                    GetMessageReasoning(choice) ?? reasoningAccum.GetContent();
                yield return new AssistantMessageEvent.Done { Message = message };
                yield break;
            }
        }
    }

    private static void FlushToolCalls(
        List<ContentBlock> contentBlocks,
        Dictionary<int, (string Id, string Name, StringBuilder Args)> toolCalls
    )
    {
        foreach (var (_, state) in toolCalls)
        {
            contentBlocks.Add(
                new ContentBlock
                {
                    Type = "tool_call",
                    Id = state.Id,
                    Name = state.Name,
                    Arguments = ParseToolArgs(state.Args.ToString()),
                }
            );
        }
        toolCalls.Clear();
    }

    private static Dictionary<string, object?> ParseToolArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return [];
        try
        {
            var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(argsJson));
            return PicoElementConverter.ObjectToDict(doc.RootElement);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string? GetMessageReasoning(PicoElement choice)
    {
        if (
            choice.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("reasoning_content", out var rc)
        )
        {
            var text = rc.GetStringOrNull();
            if (text is { Length: > 0 })
                return text;
        }
        return null;
    }

    private static string? GetContent(this StringBuilder sb) =>
        sb.Length > 0 ? sb.ToString() : null;

    /// <summary>Test-only entry point for SSE deserialization tests.</summary>
    internal static AssistantMessageEvent ParseForTest(string json) =>
        throw new NotSupportedException("Use ParseStreamAsync for tests");
}
