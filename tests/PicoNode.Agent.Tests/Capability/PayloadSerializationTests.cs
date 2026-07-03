namespace PicoNode.Agent.Tests.Capability;

using PicoJetson;
using PicoSerDe.Core;

public class PayloadSerializationTests
{
    [Test]
    public async Task HookPayload_SerializesCamelCaseKeys()
    {
        var payload = new HookPayload
        {
            Kind = "hook",
            EventName = "on_tool_call",
            ToolName = "myTool",
        };

        var json = PicoJetson.JsonSerializer.Serialize(payload);

        await Assert.That(json).Contains("\"kind\":\"hook\"");
        await Assert.That(json).Contains("\"eventName\":\"on_tool_call\"");
        await Assert.That(json).Contains("\"toolName\":\"myTool\"");
    }

    [Test]
    public async Task HookPayload_DoesNotSerializePascalCase()
    {
        var payload = new HookPayload { Kind = "hook", EventName = "on_tool_call" };

        var json = PicoJetson.JsonSerializer.Serialize(payload);

        await Assert.That(json).DoesNotContain("\"Kind\"");
        await Assert.That(json).DoesNotContain("\"EventName\"");
    }

    [Test]
    public async Task ToolCallPayload_SerializesCamelCaseKeys()
    {
        var payload = new ToolCallPayload
        {
            Kind = "tool_call",
            ToolCallId = "call_1",
            ToolName = "readFile",
        };

        var json = PicoJetson.JsonSerializer.Serialize(payload);

        await Assert.That(json).Contains("\"kind\":\"tool_call\"");
        await Assert.That(json).Contains("\"toolCallId\":\"call_1\"");
        await Assert.That(json).Contains("\"toolName\":\"readFile\"");
    }

    [Test]
    public async Task ToolCallPayload_DoesNotSerializePascalCase()
    {
        var payload = new ToolCallPayload { Kind = "tool_call", ToolCallId = "call_1" };

        var json = PicoJetson.JsonSerializer.Serialize(payload);

        await Assert.That(json).DoesNotContain("\"Kind\"");
        await Assert.That(json).DoesNotContain("\"ToolCallId\"");
    }

    [Test]
    public async Task Payloads_MatchOriginalAnonymousTypeFormat()
    {
        // The original anonymous type produced camelCase JSON like:
        // {"kind":"hook","eventName":"on_tool_call","toolName":"x"}
        // This test verifies the named payload classes preserve the same wire format.
        var hookPayload = new HookPayload
        {
            Kind = ProtocolConstants.KindHook,
            EventName = ProtocolConstants.HookEventToolCall,
            ToolName = "test",
        };

        var json = PicoJetson.JsonSerializer.Serialize(hookPayload);

        // Verify camelCase keys appear in the JSON
        await Assert.That(json.Contains("\"kind\":")).IsTrue();
        await Assert.That(json.Contains("\"eventName\":")).IsTrue();
        await Assert.That(json.Contains("\"toolName\":")).IsTrue();

        // Verify it is valid JSON that can be parsed back
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
        await Assert.That(doc.RootElement.TryGetProperty("kind", out var kind)).IsTrue();
        await Assert.That(kind.GetString()).IsEqualTo("hook");
        await Assert.That(doc.RootElement.TryGetProperty("eventName", out var evtName)).IsTrue();
        await Assert.That(evtName.GetString()).IsEqualTo("on_tool_call");
    }
}
