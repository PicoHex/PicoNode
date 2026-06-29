namespace PicoNode.Agent.Tests.Host;

public sealed class AgentHostSessionLockTests
{
    [Test]
    public async Task ProcessMessage_CreatesSessionAndReturnsResponse()
    {
        var llmClient = new FastMockLLmClient();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        var response = await host.ProcessMessageAsync("hello", model, CancellationToken.None, "s1");
        await Assert.That(response).IsNotNull();

        var msgs = await host.GetSessionMessagesAsync("s1");
        await Assert.That(msgs.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task MultipleSessions_ShouldBeIndependent()
    {
        var llmClient = new FastMockLLmClient();
        var loop = new AgentLoop(llmClient, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);
        var model = new Model { Id = "test", MaxTokens = 4096 };

        await host.ProcessMessageAsync("s1-msg", model, CancellationToken.None, "s1");
        await host.ProcessMessageAsync("s2-msg", model, CancellationToken.None, "s2");

        var msgs1 = await host.GetSessionMessagesAsync("s1");
        var msgs2 = await host.GetSessionMessagesAsync("s2");
        await Assert.That(msgs1.Count).IsGreaterThan(0);
        await Assert.That(msgs2.Count).IsGreaterThan(0);
    }

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
            await Task.CompletedTask;
        }
    }
}
