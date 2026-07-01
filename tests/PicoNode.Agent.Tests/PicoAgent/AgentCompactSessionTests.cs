using PicoNode.Agent;
using PicoNode.AI;

namespace PicoNode.Agent.Tests.PicoAgentCompactTests;

public class AgentCompactSessionTests
{
    private static global::PicoAgent.Agent CreateTestAgent(
        IAgentLlm? llm = null,
        Dictionary<string, ILLmClient>? clients = null
    )
    {
        var c = clients ?? new Dictionary<string, ILLmClient> { ["test"] = new NoopLLmClient() };
        var loop = new AgentLoop(
            llm ?? new NoopAgentLlm(),
            new CapabilityRegistry(),
            new CapabilityRunner()
        );
        var host = new AgentHost(loop);
        var router = new ProviderRouter([
            new ProviderConfig { Name = "test", ApiFormat = AiApiFormat.OpenAIChatCompletions },
        ]);
        return global::PicoAgent.Agent.CreateForTest(
            host,
            new CapabilityRegistry(),
            new Model { Id = "test-model", Provider = "test" },
            providerConfigs: c.ToDictionary(
                kv => kv.Key,
                kv => new ProviderConfig { Name = kv.Key }
            ),
            breakers: new Dictionary<string, ICircuitBreaker> { ["test"] = new CircuitBreaker() },
            clients: c
        );
    }

    [Test]
    public async Task CompactSessionAsync_NoClients_ReturnsNull()
    {
        var agent = CreateTestAgent(clients: []);
        var (entry, count, saved) = await agent.CompactSessionAsync("test");
        await Assert.That(entry).IsNull();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CompactSessionAsync_FewMessages_ReturnsNull()
    {
        var host = new AgentHost(
            new AgentLoop(new NoopAgentLlm(), new CapabilityRegistry(), new CapabilityRunner())
        );
        var agent = global::PicoAgent.Agent.CreateForTest(
            host,
            new CapabilityRegistry(),
            new Model { Id = "test-model", Provider = "test" },
            clients: new Dictionary<string, ILLmClient> { ["test"] = new NoopLLmClient() }
        );

        var session = host.GetOrCreateSession("test");
        await session.AppendMessage(new Message { Role = "user", Content = "hi" });
        await session.AppendMessage(new Message { Role = "assistant", Content = "hello" });

        var (entry, count, _) = await agent.CompactSessionAsync("test", keepRecent: 20);
        await Assert.That(entry).IsNull();
        await Assert.That(count).IsEqualTo(0);
    }

    private sealed class NoopAgentLlm : IAgentLlm
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? sp,
            Message[] msgs,
            string mid,
            string? rl,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield break;
        }
    }

    private sealed class NoopLLmClient : ILLmClient
    {
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            Model model,
            ChatContext context,
            StreamOptions? options,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            yield break;
        }
    }
}
