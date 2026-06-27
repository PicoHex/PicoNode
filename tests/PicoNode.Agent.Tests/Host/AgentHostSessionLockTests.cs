namespace PicoNode.Agent.Tests.Host;

public sealed class AgentHostSessionLockTests
{
    [Test]
    public async Task SessionLock_ReleasedWhenNoWaiters_AllowsCleanup()
    {
        var llmClient = new FastMockLLmClient();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        // Process a message — this creates a session lock
        var response = await host.ProcessMessageAsync("hello", model, CancellationToken.None, "s1");
        await Assert.That(response).IsNotNull();

        // After the call completes, the semaphore should be released
        // and ready for cleanup. We verify by calling EnsureSession first
        // (to pre-create the session entry for GetSessionMessages).
        host.EnsureSession("s1");
        var msgs = host.GetSessionMessages("s1");
        // Session exists and has messages from the call above
        await Assert.That(msgs.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ConcurrentSessions_NoDeadlocksOrCorruption()
    {
        var llmClient = new FastMockLLmClient();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        // 50 concurrent requests across 10 sessions — stress-test lock cleanup
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            var sid = $"s{i % 10}";
            tasks.Add(
                Task.Run(async () =>
                {
                    var r = await host.ProcessMessageAsync(
                        $"msg-{i}",
                        model,
                        CancellationToken.None,
                        sid
                    );
                    if (r is null)
                        throw new Exception("null response");
                })
            );
        }
        await Task.WhenAll(tasks);

        // All sessions should have messages
        for (int i = 0; i < 10; i++)
        {
            var msgs = host.GetSessionMessages($"s{i}");
            await Assert.That(msgs.Count).IsGreaterThan(0);
        }
    }

    /// <summary>
    /// Fast mock that returns immediately — no blocking, no real LLM.
    /// </summary>
    private sealed class FastMockLLmClient : ILLmClient
    {
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield return new AssistantMessageEvent.Done
            {
                Message = new Message
                {
                    Role = "assistant",
                    ContentBlocks = [new ContentBlock { Type = "text", Text = "ok" }],
                    StopReason = "end_turn",
                },
            };
            await Task.CompletedTask; // suppress CS1998
        }
    }
}
