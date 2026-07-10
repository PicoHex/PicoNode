using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentRunTurnTests
{
    private sealed class ScriptedLlmClient : ILlmClient
    {
        private readonly Queue<Message> _r = new();

        public ScriptedLlmClient AddText(string t)
        {
            _r.Enqueue(
                new Message
                {
                    Role = "assistant",
                    Content = t,
                    ContentBlocks = [new ContentBlock { Type = "text", Text = t }],
                }
            );
            return this;
        }

        public Task<Message> CompleteAsync(
            Llm l,
            List<Message> c,
            IReadOnlyList<Tool> t,
            CancellationToken ct
        ) =>
            _r.Count > 0
                ? Task.FromResult(_r.Dequeue())
                : Task.FromResult(new Message { Role = "assistant", Content = "done" });

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Llm l,
            List<Message> c,
            IReadOnlyList<Tool> t,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            if (_r.Count > 0)
            {
                var msg = _r.Dequeue();
                if (msg.Content is { Length: > 0 })
                    yield return new StreamEvent { Type = "text", Content = msg.Content };
            }
            yield return new StreamEvent { Type = "done" };
        }
    }

    [Test]
    public async Task RunTurn_SimpleText_AppendsUserAndAssistant()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new ScriptedLlmClient().AddText("Hello!");
        var tr = new FakeToolRunner();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llm, tr),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llm, tr)
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "sk",
                    },
                ],
                "x",
                "y",
                "/tmp"
            )
        );

        system.Send(agent.Id, new RunTurn("Hi"));
        await Task.Delay(200);

        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "user")).IsGreaterThan(0);
        await Assert.That(ctx.Count(m => m.Role == "assistant")).IsGreaterThan(0);
    }

    [Test]
    public async Task RunTurn_WritesToOutputChannel()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new ScriptedLlmClient().AddText("Hello!");
        var tr = new FakeToolRunner();
        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        var events = new List<ActorOutputEvent>();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llm, tr),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llm, tr)
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "sk",
                    },
                ],
                "x",
                "y",
                "/tmp"
            )
        );

        agent.OutputWriter = channel.Writer;
        _ = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
        });

        system.Send(agent.Id, new RunTurn("Hi"));
        await Task.Delay(500); // Wait for Agent to complete the writer

        await Assert.That(events.Any(e => e.Type == "text")).IsTrue();
        await Assert.That(events.Any(e => e.Type == "done")).IsTrue();
    }
}
