namespace PicoNode.Agent.Tests.Agent;

/// <summary>
/// TDD Batch 9: AgentLoop must forward tool_call_* events emitted by IAgentLlm
/// to the onEvent callback so the frontend can render tool call progress in
/// real time.
///
/// Design review flaw #3 (P1): "AgentLlmAdapter switch: 只转 text_delta, thinking_delta,
/// done, error … tool_call_start / tool_call_delta / tool_call_end 被静默丢弃".
///
/// Batch 8 taught the adapter to emit tool_call_* LlmStreamEvents. This batch
/// makes AgentLoop.CallLLMAsync forward them through onEvent so PicoAgent's
/// SSE endpoint can push them to the browser.
/// </summary>
public class AgentLoopToolCallForwardingTests
{
    [Test]
    public async Task RunTurnAsync_ToolCallStart_ForwardedToOnEvent()
    {
        var session = await BuildSessionWithUserMsg();
        var llm = new ScriptedAgentLlm([
            new LlmStreamEvent("tool_call_start", null, null, null),
            new LlmStreamEvent("done", null, "tool_use", null),
        ]);
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var events = new List<AssistantMessageEvent>();

        await loop.RunTurnAsync(
            session,
            "model",
            null,
            CancellationToken.None,
            (e, _) =>
            {
                events.Add(e);
                return ValueTask.CompletedTask;
            }
        );

        await Assert.That(events.OfType<AssistantMessageEvent.ToolCallStart>().Any()).IsTrue();
    }

    [Test]
    public async Task RunTurnAsync_ToolCallDelta_ForwardedWithArgumentText()
    {
        var session = await BuildSessionWithUserMsg();
        var llm = new ScriptedAgentLlm([
            new LlmStreamEvent("tool_call_start", null, null, null),
            new LlmStreamEvent("tool_call_delta", "{\"path\":", null, null),
            new LlmStreamEvent("tool_call_delta", "\"foo\"}", null, null),
            new LlmStreamEvent("done", null, "tool_use", null),
        ]);
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var deltas = new List<AssistantMessageEvent.ToolCallDelta>();

        await loop.RunTurnAsync(
            session,
            "model",
            null,
            CancellationToken.None,
            (e, _) =>
            {
                if (e is AssistantMessageEvent.ToolCallDelta d)
                    deltas.Add(d);
                return ValueTask.CompletedTask;
            }
        );

        await Assert.That(deltas.Count).IsEqualTo(2);
        await Assert.That(deltas[0].Delta).IsEqualTo("{\"path\":");
        await Assert.That(deltas[1].Delta).IsEqualTo("\"foo\"}");
    }

    [Test]
    public async Task RunTurnAsync_ToolCallEnd_ForwardedWithIdAndName()
    {
        var session = await BuildSessionWithUserMsg();
        var llm = new ScriptedAgentLlm([
            new LlmStreamEvent(
                "tool_call_end",
                Text: null,
                StopReason: null,
                ErrorMessage: null,
                ToolCallId: "call_42",
                ToolName: "read_file"
            ),
            new LlmStreamEvent("done", null, "tool_use", null),
        ]);
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var ends = new List<AssistantMessageEvent.ToolCallEnd>();

        await loop.RunTurnAsync(
            session,
            "model",
            null,
            CancellationToken.None,
            (e, _) =>
            {
                if (e is AssistantMessageEvent.ToolCallEnd te)
                    ends.Add(te);
                return ValueTask.CompletedTask;
            }
        );

        await Assert.That(ends.Count).IsEqualTo(1);
        await Assert.That(ends[0].Call.Id).IsEqualTo("call_42");
        await Assert.That(ends[0].Call.Name).IsEqualTo("read_file");
    }

    private static async Task<PicoNode.Agent.Session> BuildSessionWithUserMsg()
    {
        var session = new PicoNode.Agent.Session(new InMemorySessionStorage());
        await session.AppendMessage(
            new Message
            {
                Role = "user",
                Content = "hi",
                Timestamp = 1,
            }
        );
        return session;
    }

    private sealed class ScriptedAgentLlm : IAgentLlm
    {
        private readonly LlmStreamEvent[] _script;

        public ScriptedAgentLlm(LlmStreamEvent[] script) => _script = script;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            foreach (var e in _script)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }
}
