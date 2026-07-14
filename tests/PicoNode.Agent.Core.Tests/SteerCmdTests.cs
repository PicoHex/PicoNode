using System.Runtime.CompilerServices;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.AI;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class SteerCmdTests
{
    private sealed class TwoPhaseLlm : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task<Message> CompleteAsync(
            Llm l,
            List<Message> c,
            IReadOnlyList<Tool> t,
            CancellationToken ct
        ) => Task.FromResult(new Message());

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Llm l,
            List<Message> c,
            IReadOnlyList<Tool> t,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            CallCount++;
            if (CallCount == 1)
            {
                yield return new StreamEvent
                {
                    Type = "tool_call_end",
                    ToolCallId = "0",
                    ToolName = "bash",
                };
            }
            else
            {
                yield return new StreamEvent { Type = "text", Content = $"resp-{CallCount}" };
            }
            yield return new StreamEvent { Type = "done" };
        }
    }

    private static async Task<DomainAgent> CreateRunningAgentAsync(
        ActorSystem system,
        ILlmClient llm
    )
    {
        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llm, new FakeToolRunner()),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llm, new FakeToolRunner())
        );
        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [new() { ProviderName = "x", ModelId = "y", ApiKey = "k" }],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        return agent;
    }

    [Test]
    public async Task SteerCmd_AppendsUserMessageToSession()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new TwoPhaseLlm();
        var agent = await CreateRunningAgentAsync(system, llm);

        // No active run — send steer
        system.Send(agent.Id, new SteerCmd("please also check X"));
        await Task.Delay(100);

        var ctx = await agent.Session!.BuildContext();
        await Assert
            .That(ctx.Any(m => m.Role == "user" && m.Content == "please also check X"))
            .IsTrue();
    }

    [Test]
    public async Task SteerCmd_DuringRun_IsInjectedBeforeNextTurn()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llm = new TwoPhaseLlm();
        var agent = await CreateRunningAgentAsync(system, llm);

        system.Send(agent.Id, new RunTurn("do work", "t1"));
        await Task.Delay(100); // turn 1 starts (tool call)
        // Inject steer during turn 1 execution — it queues before CheckContinue
        system.Send(agent.Id, new SteerCmd("also check Y"));
        await Task.Delay(600);

        // steer message should be in session, and CheckContinue saw user leaf → triggered response turn
        var ctx = await agent.Session!.BuildContext();
        await Assert
            .That(ctx.Any(m => m.Role == "user" && m.Content == "also check Y"))
            .IsTrue();
        // llm called at least twice (turn1 tool + response to steer)
        await Assert.That(llm.CallCount).IsGreaterThanOrEqualTo(2);
    }
}
