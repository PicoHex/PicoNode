namespace PicoNode.Tests;

public sealed class AgentResultTests
{
    [Test]
    public async Task Constructor_SetsRequiredProperties()
    {
        var msgs = new List<Message>
        {
            new() { Role = "assistant", Content = "hello" },
        };
        var result = new AgentResult("hello", msgs);
        await Assert.That(result.Text).IsEqualTo("hello");
        await Assert.That(result.NewMessages).IsEqualTo(msgs);
    }

    [Test]
    public async Task Constructor_WithOptionalProps()
    {
        var msgs = new List<Message>();
        var usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 };
        var result = new AgentResult("ok", msgs) { StopReason = "end_turn", Usage = usage };
        await Assert.That(result.StopReason).IsEqualTo("end_turn");
        await Assert.That(result.Usage).IsEqualTo(usage);
    }
}
