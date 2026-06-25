namespace PicoNode.AI;

public sealed class FormatConverter : IFormatConverter
{
    // v1: pass-through stub. Full Anthropic ↔ OpenAI conversion in v2.
    // See spec: 2026-06-24-picorelay-design.md Section 3.3
    public bool CanConvertRequest(AiApiFormat from, AiApiFormat to) => from != to;

    public bool CanConvertResponse(AiApiFormat from, AiApiFormat to) => from != to;

    public async IAsyncEnumerable<AssistantMessageEvent> ConvertStream(
        IAsyncEnumerable<AssistantMessageEvent> source,
        AiApiFormat targetFormat,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        // v1: pass-through stub
        await foreach (var evt in source.WithCancellation(ct))
            yield return evt;
    }
}
