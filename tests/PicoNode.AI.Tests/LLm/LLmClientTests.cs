namespace PicoNode.AI.Tests.LLm;


public class SseParserTests
{
    [Test]
    public async Task Parse_TextDelta_EmitsCorrectEvents()
    {
        var sseData =
            """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (
            var evt in SseParser.ParseAnthropicStreamAsync(
                stream,
                "claude-sonnet-4",
                CancellationToken.None
            )
        )
        {
            events.Add(evt);
        }

        await Assert.That(events.Count).IsGreaterThan(0);
        var textDeltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(textDeltas.Length).IsEqualTo(2);
        await Assert.That(textDeltas[1].Delta).IsEqualTo(" world");
        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ToolCall_EmitsToolCallEvents()
    {
        var sseData =
            """
            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tc_1","name":"read","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\":\"/foo\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (
            var evt in SseParser.ParseAnthropicStreamAsync(
                stream,
                "claude-sonnet-4",
                CancellationToken.None
            )
        )
        {
            events.Add(evt);
        }

        var toolStarts = events.OfType<AssistantMessageEvent.ToolCallStart>().ToArray();
        await Assert.That(toolStarts.Length).IsEqualTo(1);
        var toolDeltas = events.OfType<AssistantMessageEvent.ToolCallDelta>().ToArray();
        await Assert.That(toolDeltas.Length).IsEqualTo(2);
        var toolEnds = events.OfType<AssistantMessageEvent.ToolCallEnd>().ToArray();
        await Assert.That(toolEnds.Length).IsEqualTo(1);
        await Assert.That(toolEnds[0].Call.Name).IsEqualTo("read");
    }

    [Test]
    public async Task Parse_EmptyStream_ReturnsNothing()
    {
        using var stream = new MemoryStream([]);
        var count = 0;

        await foreach (
            var _ in SseParser.ParseAnthropicStreamAsync(
                stream,
                "claude-sonnet-4",
                CancellationToken.None
            )
        )
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_MalformedJsonLine_SkipsAndContinues()
    {
        var sseData =
            """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: NOT_VALID_JSON

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (
            var evt in SseParser.ParseAnthropicStreamAsync(
                stream,
                "claude-sonnet-4",
                CancellationToken.None
            )
        )
        {
            events.Add(evt);
        }

        // Should not crash; should emit text and done events
        var textDeltas = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(textDeltas.Length).IsEqualTo(1);
        await Assert.That(textDeltas[0].Delta).IsEqualTo("Hello");
        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_OutOfOrderBlockIndices_HandlesCorrectly()
    {
        // Send block index 1 before index 0 — should still work
        var sseData =
            """
            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tc_1","name":"read","input":{}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"/foo\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_stop
            data: {"type":"message_stop"}

            """u8.ToArray();

        using var stream = new MemoryStream(sseData);
        var events = new List<AssistantMessageEvent>();

        await foreach (
            var evt in SseParser.ParseAnthropicStreamAsync(
                stream,
                "claude-sonnet-4",
                CancellationToken.None
            )
        )
        {
            events.Add(evt);
        }

        // Both blocks should be present, text at [0], tool_use at [1]
        var done = events.OfType<AssistantMessageEvent.Done>().ToArray();
        await Assert.That(done.Length).IsEqualTo(1);
        await Assert.That(done[0].Message.ContentBlocks!.Length).IsEqualTo(2);
        await Assert.That(done[0].Message.ContentBlocks[0].Type).IsEqualTo("text");
        await Assert.That(done[0].Message.ContentBlocks[1].Type).IsEqualTo("tool_call");
    }
}

public class AnthropicLLmClientTests
{
    [Test]
    public async Task CompleteAsync_ReturnsFinalMessage()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    event: message_start
                    data: {"type":"message_start","message":{}}

                    event: content_block_start
                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

                    event: content_block_stop
                    data: {"type":"content_block_stop","index":0}

                    event: message_delta
                    data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

                    event: message_stop
                    data: {"type":"message_stop"}

                    """
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var result = await client.CompleteAsync(
            new Model
            {
                Id = "claude-sonnet-4",
                BaseUrl = "https://api.anthropic.com",
                Api = AiApiFormat.AnthropicMessages,
                MaxTokens = 4096,
            },
            new ChatContext
            {
                Messages = new[]
                {
                    new Message
                    {
                        Role = "user",
                        Content = "Hi",
                        Timestamp = 1,
                    },
                },
            }
        );

        await Assert.That(result).IsNotNull();
        await Assert.That(result.StopReason).IsEqualTo("end_turn");
        await Assert.That(result.ContentBlocks!.Length).IsEqualTo(1);
        await Assert.That(result.ContentBlocks[0].Text).IsEqualTo("Hello");
    }

    [Test]
    public async Task StreamAsync_HttpError_EmitsErrorEvent()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Rate limited\"}}"
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        var events = new List<AssistantMessageEvent>();
        await foreach (
            var evt in client.StreamAsync(
                new Model
                {
                    Id = "claude-sonnet-4",
                    BaseUrl = "https://api.anthropic.com",
                    Api = AiApiFormat.AnthropicMessages,
                    MaxTokens = 4096,
                },
                new ChatContext
                {
                    Messages = new[]
                    {
                        new Message
                        {
                            Role = "user",
                            Content = "Hi",
                            Timestamp = 1,
                        },
                    },
                },
                null,
                CancellationToken.None
            )
        )
        {
            events.Add(evt);
        }

        await Assert.That(events.Count).IsEqualTo(1);
        var error = events[0] as AssistantMessageEvent.Error;
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message.ErrorMessage).IsEqualTo("Rate limited");
        await Assert.That(error.Message.StopReason).IsEqualTo("error");
    }

    [Test]
    public async Task StreamAsync_UsesProviderEnvVar_WhenNoApiKeyInOptions()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-key-123");

            await foreach (
                var _ in client.StreamAsync(
                    new Model
                    {
                        Id = "claude-sonnet-4",
                        Provider = "anthropic",
                        BaseUrl = "https://api.anthropic.com",
                        Api = AiApiFormat.AnthropicMessages,
                        MaxTokens = 4096,
                    },
                    new ChatContext
                    {
                        Messages = new[]
                        {
                            new Message
                            {
                                Role = "user",
                                Content = "Hi",
                                Timestamp = 1,
                            },
                        },
                    },
                    null,
                    CancellationToken.None
                )
            ) { }

            await Assert.That(handler.LastRequest).IsNotNull();
            var authHeader = handler.LastRequest!.Headers.GetValues("x-api-key").FirstOrDefault();
            await Assert.That(authHeader).IsEqualTo("env-key-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Test]
    public async Task EscapeString_HandlesControlCharacters()
    {
        var handler = new MockHttpHandler
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                ),
            },
        };
        var httpClient = new HttpClient(handler);
        var client = new AnthropicLLmClient(httpClient);

        await foreach (
            var _ in client.StreamAsync(
                new Model
                {
                    Id = "claude-sonnet-4",
                    BaseUrl = "https://api.anthropic.com",
                    Api = AiApiFormat.AnthropicMessages,
                    MaxTokens = 4096,
                },
                new ChatContext
                {
                    SystemPrompt = "Prompt\u0000with\u0000nulls",
                    Messages = new[]
                    {
                        new Message
                        {
                            Role = "user",
                            Content = "Tab\there",
                            Timestamp = 1,
                        },
                    },
                },
                null,
                CancellationToken.None
            )
        ) { }

        await Assert.That(handler.CapturedRequestBody).IsNotNull();
        using var doc = System.Text.Json.JsonDocument.Parse(handler.CapturedRequestBody!);
        await Assert
            .That(doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString())
            .IsEqualTo("Tab\there");
    }
}
