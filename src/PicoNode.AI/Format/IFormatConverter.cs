namespace PicoNode.AI;

public interface IFormatConverter
{
    bool CanConvertRequest(AiApiFormat from, AiApiFormat to);
    bool CanConvertResponse(AiApiFormat from, AiApiFormat to);
    IAsyncEnumerable<AssistantMessageEvent> ConvertStream(
        IAsyncEnumerable<AssistantMessageEvent> source,
        AiApiFormat targetFormat,
        CancellationToken ct);
}
