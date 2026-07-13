using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Tests that tool execution errors are captured as tool_result content
/// rather than propagating as exceptions that kill the turn.
/// </summary>
public sealed class AgentToolErrorRecoveryTests
{
    private sealed class ThrowingToolRunner : IToolRunner
    {
        public string LastError { get; private set; } = "";

        public Task<string> ExecuteAsync(
            string name,
            Dictionary<string, object?> args,
            CancellationToken ct
        )
        {
            LastError = $"[ToolError: InvalidOperationException] Simulated failure for '{name}'";
            throw new InvalidOperationException($"Simulated failure for '{name}'");
        }
    }

    private sealed class ToolCallLlmClient : ILlmClient
    {
        private int _calls;

        public Task<Message> CompleteAsync(
            Llm llm,
            List<Message> context,
            IReadOnlyList<Tool> tools,
            CancellationToken ct
        ) => Task.FromResult(new Message());

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Llm llm,
            List<Message> context,
            IReadOnlyList<Tool> tools,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            if (++_calls == 1)
            {
                // First call: emit a tool call that will trigger the failing tool runner
                yield return new StreamEvent
                {
                    Type = "tool_call_end",
                    ToolCallId = "0",
                    ToolName = "bash",
                };
            }
            yield return new StreamEvent { Type = "done" };
        }
    }

    [Test]
    public async Task ToolError_IsAppendedAsToolResult_NotThrownToCaller()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new ToolCallLlmClient();
        var toolRunner = new ThrowingToolRunner();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // RunTurn with tool call — should complete WITHOUT throwing
        // (tool error is captured in session, not propagated)
        system.Send(agent.Id, new RunTurn("run failing tool", "t1"));
        await Task.Delay(300);

        // Verify Session contains the tool_result with error info
        var ctx = await agent.Session!.BuildContext();
        var toolResults = ctx.Where(m => m.Role == "toolResult").ToList();

        await Assert.That(toolResults.Count).IsGreaterThan(0);
        var resultText = toolResults[0].ContentBlocks?[0].Text ?? "";
        await Assert.That(resultText).Contains("ToolError");
    }

    [Test]
    public async Task ToolError_TurnStillCompletes_Normally()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new ToolCallLlmClient();
        var toolRunner = new ThrowingToolRunner();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // RunTurn should not throw — Verify by asking after
        var result = await system.AskAsync<object?>(agent.Id, new RunTurn("fail me", "t2"));

        // Agent should still be Running (not Failed)
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task ToolError_OutputChannel_OnlyEmitsToolResult()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new ToolCallLlmClient();
        var toolRunner = new ThrowingToolRunner();
        var channel = Channel.CreateUnbounded<ActorOutputEvent>();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        agent.OutputWriter = channel.Writer;
        var events = new List<ActorOutputEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                events.Add(e);
        });

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        system.Send(agent.Id, new RunTurn("fail", "t3"));
        await Task.Delay(500);

        // Tool failure should emit tool_result, NOT a separate tool_error
        var toolEvents = events.Where(e => e.Type is "tool_result" or "tool_error").ToList();

        await Assert.That(toolEvents.Count).IsEqualTo(1);
        await Assert.That(toolEvents[0].Type).IsEqualTo("tool_result");
        await Assert.That(toolEvents[0].Data!).Contains("ToolError");
    }
}
