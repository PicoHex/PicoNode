using PicoNode.Agent.Domain;
using PicoNode.AI;
using PicoNode.AI.Types;

namespace PicoNode.Agent.Core.Tests;

internal sealed class FakeLlmClient : ILlmClient
{
    public Task<Message> CompleteAsync(Llm llm, List<Message> context, IReadOnlyList<Tool> tools, CancellationToken ct)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamEvent> StreamAsync(Llm llm, List<Message> context, IReadOnlyList<Tool> tools, CancellationToken ct)
        => throw new NotSupportedException();
}

internal sealed class FakeToolRunner : IToolRunner
{
    public Task<string> ExecuteAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct)
        => Task.FromResult("fake result");
}
