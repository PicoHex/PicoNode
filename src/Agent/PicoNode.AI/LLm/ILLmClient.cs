namespace PicoNode.AI;

public interface ILLmClient
{
    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        CancellationToken ct
    );
}

public static class LLmClientExtensions
{
    public static async ValueTask<Message> CompleteAsync(
        this ILLmClient client,
        Model model,
        ChatContext context,
        StreamOptions? options = null,
        CancellationToken ct = default
    )
    {
        await foreach (var evt in client.StreamAsync(model, context, options, ct))
        {
            if (evt is AssistantMessageEvent.Done d)
                return d.Message;
            if (evt is AssistantMessageEvent.Error e)
                return e.Message;
        }
        throw new InvalidOperationException("Stream ended without Done or Error event.");
    }
}
