namespace PicoNode.Agent.Tests;

using NetAI = PicoNode.AI;
using NetAITypes = PicoNode.AI.Types;

public class LlmClientAdapterTests
{
    [Test]
    public async Task CompleteAsync_DelegatesToInnerClient()
    {
        var capturedModel = new List<NetAI.Model>();
        var capturedMessages = new List<NetAI.Message[]>();
        var innerClient = new CaptureLlmClient(
            (model, msgs, _) =>
            {
                capturedModel.Add(model);
                capturedMessages.Add(msgs);
                return Task.CompletedTask;
            }
        );

        var llm = new Llm
        {
            ProviderName = "deepseek",
            ModelId = "deepseek-chat",
            ApiKey = "sk-xxx",
            BaseUrl = "https://api.deepseek.com/v1",
            ThinkingLevel = ThinkingLevel.High,
            MaxTokens = 4096,
            ThinkingEnabled = true,
        };
        var adapter = new LlmClientAdapter(innerClient);
        var context = new List<Message>
        {
            new() { Role = "user", Content = "hello" },
        };

        await adapter.CompleteAsync(llm, context, [], CancellationToken.None);

        await Assert.That(capturedModel.Count).IsEqualTo(1);
        await Assert.That(capturedModel[0].Id).IsEqualTo("deepseek-chat");
        await Assert.That(capturedModel[0].Provider).IsEqualTo("deepseek");
    }

    [Test]
    public async Task CompleteAsync_StreamsToCompletion()
    {
        var innerClient = new SimpleLlmClient();
        var llm = new Llm
        {
            ProviderName = "test",
            ModelId = "test",
            ApiKey = "sk-xxx",
            MaxTokens = 100,
        };

        var adapter = new LlmClientAdapter(innerClient);
        var context = new List<Message>
        {
            new() { Role = "user", Content = "hello" },
        };
        var result = await adapter.CompleteAsync(llm, context, [], CancellationToken.None);

        await Assert.That(result.Role).IsEqualTo("assistant");
        await Assert.That(result.ContentBlocks).IsNotNull();
    }
}

internal sealed class CaptureLlmClient : NetAI.ILLmClient
{
    private readonly Func<NetAI.Model, NetAI.Message[], NetAI.StreamOptions?, Task> _onStream;

    public CaptureLlmClient(Func<NetAI.Model, NetAI.Message[], NetAI.StreamOptions?, Task> onStream)
    {
        _onStream = onStream;
    }

    public async IAsyncEnumerable<NetAI.AssistantMessageEvent> StreamAsync(
        NetAI.Model model,
        NetAITypes.ChatContext context,
        NetAI.StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await _onStream(model, context.Messages, options);
        yield return new NetAI.AssistantMessageEvent.Done
        {
            Message = new NetAI.Message
            {
                Role = "assistant",
                ContentBlocks = [new NetAI.ContentBlock { Type = "text", Text = "response" }],
                StopReason = "end_turn",
            },
        };
    }
}

internal sealed class SimpleLlmClient : NetAI.ILLmClient
{
    public async IAsyncEnumerable<NetAI.AssistantMessageEvent> StreamAsync(
        NetAI.Model model,
        NetAITypes.ChatContext context,
        NetAI.StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return new NetAI.AssistantMessageEvent.Done
        {
            Message = new NetAI.Message
            {
                Role = "assistant",
                ContentBlocks = [new NetAI.ContentBlock { Type = "text", Text = "Hello from LLM" }],
                StopReason = "end_turn",
            },
        };
    }
}
