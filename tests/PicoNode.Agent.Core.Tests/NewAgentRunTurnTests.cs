using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class NewAgentRunTurnTests
{
    private sealed class ScriptedLlmClient : ILlmClient
    {
        private readonly Queue<Message> _responses = new();
        public ScriptedLlmClient AddText(string text)
        {
            _responses.Enqueue(new Message { Role = "assistant", Content = text,
                ContentBlocks = [new ContentBlock { Type = "text", Text = text }] });
            return this;
        }
        public ScriptedLlmClient AddToolCall(string name, Dictionary<string, object?> args)
        {
            _responses.Enqueue(new Message { Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "tool_call", Name = name,
                    Arguments = args, Id = Guid.CreateVersion7().ToString() }] });
            return this;
        }
        public Task<Message> CompleteAsync(Llm llm, List<Message> ctx,
            IReadOnlyList<Tool> tools, CancellationToken ct)
            => _responses.Count > 0
                ? Task.FromResult(_responses.Dequeue())
                : Task.FromResult(new Message { Role = "assistant", Content = "done" });
    }

    private static List<Llm> DefaultLlms() =>
    [new() { ProviderName = "deepseek", ModelId = "deepseek-chat", ApiKey = "sk-test" }];

    [Test]
    public async Task RunTurn_SimpleText_AppendsUserAndAssistant()
    {
        var llm = new ScriptedLlmClient().AddText("Hello!");
        var (system, agent) = await CreateSystemAndAgent(llm);

        system.Send(agent.Id, new RunTurn("Hi"));
        await Task.Delay(200);

        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "user")).IsGreaterThan(0);
        await Assert.That(ctx.Count(m => m.Role == "assistant")).IsGreaterThan(0);
    }

    [Test]
    public async Task RunTurn_ToolCall_ExecutesAndLoops()
    {
        var llm = new ScriptedLlmClient()
            .AddToolCall("bash", new Dictionary<string, object?> { ["command"] = "echo hi" })
            .AddText("Done!");
        var (system, agent) = await CreateSystemAndAgent(llm);

        system.Send(agent.Id, new RunTurn("Run"));
        await Task.Delay(300);

        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "toolResult")).IsGreaterThan(0);
    }

    private static async Task<(ActorSystem System, NewAgent Agent)> CreateSystemAndAgent(ILlmClient llm)
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var toolRunner = new FakeToolRunner();
        system.Register<NewAgent>(
            cmd => cmd switch { CreateAgent c => new NewAgent(c, llm, toolRunner), _ => throw new InvalidOperationException() },
            () => new NewAgent(llm, toolRunner));
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms(), "deepseek", "deepseek-chat", "/tmp"));
        return (system, agent);
    }
}
