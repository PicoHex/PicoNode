using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class OutputWriterCompletionTests
{
    [Test]
    [Timeout(5000)]
    public async Task RunTurn_CompletesOutputWriter()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new FakeTextLlm("Hi!");
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

        var channel = Channel.CreateUnbounded<ActorOutputEvent>();
        agent.OutputWriter = channel.Writer;

        system.Send(agent.Id, new RunTurn("Hi"));

        // Must complete — Writer.Complete() is called by Agent
        var cts = new CancellationTokenSource(3000);
        try
        {
            var list = await channel.Reader.ReadAllAsync(cts.Token).ToListAsync();
            await Assert.That(list.Count).IsGreaterThan(0);
        }
        catch (OperationCanceledException)
        {
            await Assert.That(false).IsTrue(); // Timeout — Writer was never completed!
        }
    }
}
