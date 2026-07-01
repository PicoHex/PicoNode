using System.Collections.Concurrent;

namespace PicoNode.Agent.Tests.Agent;

/// <summary>
/// Batch 3 (A7): AgentLoop must expose a per-call API for modelId/systemPrompt
/// so concurrent invocations across sessions do not mutate shared state.
/// </summary>
public class AgentLoopPerCallIsolationTests
{
    [Test]
    public async Task RunTurnAsync_PerCallModelId_IsUsedWithoutMutatingSharedState()
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

        var llm = new CapturingLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var beforeModelId = loop.ModelId;

        await loop.RunTurnAsync(
            session,
            modelId: "per-call-model",
            systemPrompt: null,
            CancellationToken.None
        );

        await Assert.That(llm.LastModelId).IsEqualTo("per-call-model");
        // Shared state must not be mutated by the per-call overload.
        await Assert.That(loop.ModelId).IsEqualTo(beforeModelId);
    }

    [Test]
    public async Task RunTurnAsync_PerCallSystemPrompt_IsUsedWithoutMutatingSharedState()
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

        var llm = new CapturingLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var beforePrompt = loop.SystemPrompt;

        await loop.RunTurnAsync(
            session,
            modelId: "m",
            systemPrompt: "PER-CALL-PROMPT",
            CancellationToken.None
        );

        await Assert.That(llm.LastSystemPrompt).IsEqualTo("PER-CALL-PROMPT");
        await Assert.That(loop.SystemPrompt).IsEqualTo(beforePrompt);
    }

    [Test]
    public async Task ProcessMessageAsync_ConcurrentDifferentSessions_UseCorrectModelIdEach()
    {
        var llm = new RecordingLlm();
        var loop = new AgentLoop(llm, new CapabilityRegistry(), new CapabilityRunner());
        var host = new AgentHost(loop);

        var gateA = new GatedStorage();
        var gateB = new GatedStorage();
        await host.RestoreSessionAsync("sA", new PicoNode.Agent.Session(gateA));
        await host.RestoreSessionAsync("sB", new PicoNode.Agent.Session(gateB));

        var modelA = new Model { Id = "model-A" };
        var modelB = new Model { Id = "model-B" };

        var taskA = Task.Run(() =>
            host.ProcessMessageAsync("hi-A", modelA, CancellationToken.None, "sA")
        );
        await gateA.PathToRootReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var taskB = Task.Run(() =>
            host.ProcessMessageAsync("hi-B", modelB, CancellationToken.None, "sB")
        );
        await gateB.PathToRootReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Both tasks are paused inside GetPathToRoot. With the shared-state bug, both would
        // proceed to read _loop.ModelId == "model-B" (last write wins) and pass it to the LLM.
        gateA.Release.SetResult();
        gateB.Release.SetResult();
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(10));

        var callsByUser = llm.Calls.ToDictionary(x => x.UserContent, x => x.ModelId);
        await Assert.That(callsByUser["hi-A"]).IsEqualTo("model-A");
        await Assert.That(callsByUser["hi-B"]).IsEqualTo("model-B");
    }

    // ── test doubles ──

    private sealed class CapturingLlm : IAgentLlm
    {
        public string? LastSystemPrompt { get; private set; }
        public string? LastModelId { get; private set; }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            LastSystemPrompt = systemPrompt;
            LastModelId = modelId;
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingLlm : IAgentLlm
    {
        public ConcurrentBag<(string UserContent, string ModelId)> Calls { get; } = new();

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            string? systemPrompt,
            Message[] messages,
            string modelId,
            string? reasoningLevel,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            Calls.Add((lastUser, modelId));
            yield return new LlmStreamEvent("done", null, "end_turn", null);
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Wraps InMemorySessionStorage and gates <see cref="GetPathToRoot"/> so the test can
    /// hold two concurrent RunTurnAsync calls simultaneously past the point where AgentHost
    /// mutates any shared loop state.
    /// </summary>
    private sealed class GatedStorage : ISessionStorage
    {
        private readonly InMemorySessionStorage _inner = new();
        public TaskCompletionSource PathToRootReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionTreeEntryBase[]> GetPathToRoot(string? leafId)
        {
            PathToRootReached.TrySetResult();
            await Release.Task;
            return await _inner.GetPathToRoot(leafId);
        }

        public Task<string?> GetLeafId() => _inner.GetLeafId();

        public Task SetLeafId(string? leafId) => _inner.SetLeafId(leafId);

        public Task<string> CreateEntryId() => _inner.CreateEntryId();

        public Task AppendEntry(SessionTreeEntryBase entry) => _inner.AppendEntry(entry);

        public Task<SessionTreeEntryBase?> GetEntry(string id) => _inner.GetEntry(id);

        public Task<SessionTreeEntryBase[]> GetEntries() => _inner.GetEntries();

        public Task<string?> GetLabel(string id) => _inner.GetLabel(id);
    }
}
