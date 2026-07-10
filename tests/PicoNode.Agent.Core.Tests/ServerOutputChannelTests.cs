using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: Server RunTurn via OutputChannel (no callback in command).
/// Agent writes output events, Server reads and streams to SSE.
/// </summary>
public sealed class ServerOutputChannelTests
{
    [Test]
    public async Task RunTurn_ViaOutputChannel_StreamsEvents()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new FakeTextLlm("Hello!");
        var tr = new FakeToolRunner();

        system.Register<DomainAgent>(
            cmd => cmd switch { CreateAgent c => new DomainAgent(c, llm, tr), _ => throw new InvalidOperationException() },
            () => new DomainAgent(llm, tr));

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent([new() { ProviderName = "x", ModelId = "y", ApiKey = "sk" }], "x", "y", "/tmp"));

        // Simulate Server: create Channel, set OutputWriter, run background reader
        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        agent.OutputWriter = channel.Writer;

        var events = new List<ActorOutputEvent>();
        var readerTask = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
        });

        // Send RunTurn — no callback, pure command
        system.Send(agent.Id, new RunTurn("Hi"));
        await Task.Delay(300);

        // Simulate RunTurn complete — Agent writes "done"
        await Task.Delay(100);
        channel.Writer.Complete();
        await readerTask;

        await Assert.That(events.Any(e => e.Type == "text")).IsTrue();
        await Assert.That(events.Any(e => e.Type == "done")).IsTrue();
    }

    [Test]
    public async Task RunTurn_OutputChannelIndependence_AgentContinues()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new FakeTextLlm("Hello!");
        var tr = new FakeToolRunner();

        system.Register<DomainAgent>(
            cmd => cmd switch { CreateAgent c => new DomainAgent(c, llm, tr), _ => throw new InvalidOperationException() },
            () => new DomainAgent(llm, tr));

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent([new() { ProviderName = "x", ModelId = "y", ApiKey = "sk" }], "x", "y", "/tmp"));

        // No OutputWriter set — Agent should still complete RunTurn normally
        system.Send(agent.Id, new RunTurn("Hi"));
        await Task.Delay(200);

        var ctx = await agent.Session!.BuildContext();
        await Assert.That(ctx.Count(m => m.Role == "user")).IsGreaterThan(0);
        await Assert.That(ctx.Count(m => m.Role == "assistant")).IsGreaterThan(0);
    }
}

/// <summary>Fake LLM client that returns a fixed text response.</summary>
internal sealed class FakeTextLlm : ILlmClient
{
    private readonly string _text;
    public FakeTextLlm(string text) => _text = text;

    public Task<Message> CompleteAsync(Llm llm, List<Message> context,
        IReadOnlyList<Tool> tools, CancellationToken ct)
        => Task.FromResult(new Message { Role = "assistant", Content = _text,
            ContentBlocks = [new ContentBlock { Type = "text", Text = _text }] });
}
