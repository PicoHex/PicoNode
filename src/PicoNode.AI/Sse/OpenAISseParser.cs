namespace PicoNode.AI;

public static class OpenAISseParser
{
    public static async IAsyncEnumerable<AssistantMessageEvent> ParseStreamAsync(
        Stream stream,
        string model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var contentAccum = new StringBuilder();
        var message = new Message { Role = "assistant", Model = model, Provider = "openai", Api = AiApiFormat.OpenAIChatCompletions };

        bool started = false;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (!line.StartsWith("data: ")) continue;

            var json = line[6..]; // skip "data: "

            // Terminal signal
            if (json.Trim() == "[DONE]")
            {
                message.ContentBlocks = [new ContentBlock { Type = "text", Text = contentAccum.ToString() }];
                message.StopReason = "stop";
                yield return new AssistantMessageEvent.Done { Message = message };
                yield break;
            }

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using var _doc = doc;
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            if (delta.TryGetProperty("content", out var content))
            {
                var text = content.GetString()!;
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
                        ContentBlocks = [new ContentBlock { Type = "text", Text = contentAccum.ToString() }],
                    },
                };
            }

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() is { } reason && reason != "null" && reason != "")
            {
                message.ContentBlocks = [new ContentBlock { Type = "text", Text = contentAccum.ToString() }];
                message.StopReason = reason;
                yield return new AssistantMessageEvent.Done { Message = message };
                yield break;
            }
        }
    }
}
