using System.Text;
using PicoJetson;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: Server.cs HTTP response DTO serialization via PicoJetson.
/// Every DTO replaces a hand-crafted JSON string in Server.AddEndpoints().
/// </summary>
public sealed class ResponseDtoSerializationTests
{
    [Test]
    public async Task HealthResponse_RoundTrip()
    {
        var dto = new HealthResponse
        {
            Status = "ok",
            Model = "deepseek-chat",
            Provider = "deepseek",
        };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"status\"");
        await Assert.That(json).Contains("\"model\"");
        await Assert.That(json).Contains("\"provider\"");
        await Assert.That(json).Contains("\"ok\"");
        await Assert.That(json).Contains("\"deepseek-chat\"");
        await Assert.That(json).Contains("\"deepseek\"");
    }

    [Test]
    public async Task ConfigStatusResponse_RoundTrip()
    {
        var dto = new ConfigStatusResponse
        {
            Configured = true,
            Model = "gpt-4o",
            Provider = "openai",
            Providers = ["openai", "anthropic"],
            ThinkingEnabled = true,
            ThinkingLevel = "xhigh",
            MaxTokens = 4096,
        };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"configured\"");
        await Assert.That(json).Contains("true");
        await Assert.That(json).Contains("\"openai\"");
        await Assert.That(json).Contains("\"anthropic\"");
        await Assert.That(json).Contains("\"xhigh\"");
    }

    [Test]
    public async Task ProviderTemplate_RoundTrip()
    {
        var dto = new ProviderTemplate
        {
            Name = "openai",
            Label = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            ApiFormat = "openai",
        };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"name\"");
        await Assert.That(json).Contains("\"label\"");
        await Assert.That(json).Contains("\"baseUrl\"");
        await Assert.That(json).Contains("\"apiFormat\"");
    }

    [Test]
    public async Task PromptResponse_RoundTrip()
    {
        var dto = new PromptResponse { Prompt = "You are an AI assistant." };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"prompt\"");
        await Assert.That(json).Contains("AI assistant");
    }

    [Test]
    public async Task CompactResponse_RoundTrip()
    {
        var dto = new CompactResponse
        {
            CompressedCount = 10,
            Summary = "Summarized context...",
            TokensSaved = 500,
        };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"compressedCount\"");
        await Assert.That(json).Contains("\"summary\"");
        await Assert.That(json).Contains("\"tokensSaved\"");
        await Assert.That(json).Contains("500");
    }

    [Test]
    public async Task ModelListItem_RoundTrip()
    {
        var dto = new ModelListItem { Id = "gpt-4o", OwnedBy = "openai" };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"id\"");
        await Assert.That(json).Contains("\"ownedBy\"");
        await Assert.That(json).Contains("\"gpt-4o\"");
    }

    [Test]
    public async Task PromptWithSpecialChars_HandlesCorrectly()
    {
        var dto = new PromptResponse
        {
            Prompt = "Line1\nLine2\r\nTab\there \"quoted\" \\backslash",
        };
        var json = JsonSerializer.Serialize(dto);

        var deserialized = JsonSerializer.Deserialize<PromptResponse>(Encoding.UTF8.GetBytes(json));
        await Assert.That(deserialized!.Prompt).IsEqualTo(dto.Prompt);
    }

    [Test]
    public async Task EmptyProviders_SerializesAsEmptyArray()
    {
        var dto = new ConfigStatusResponse
        {
            Configured = false,
            Model = "",
            Provider = "",
            Providers = [],
            ThinkingEnabled = false,
            ThinkingLevel = "",
            MaxTokens = 0,
        };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"providers\":[]");
    }

    // ── StatusResponse (replaces anonymous type in JsonHelper.Ok) ──

    [Test]
    public async Task StatusResponse_SerializesAsJson()
    {
        var dto = new StatusResponse { Status = "ok" };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"status\"");
        await Assert.That(json).Contains("\"ok\"");
    }

    // ── ErrorResponse (replaces anonymous type in JsonHelper.Error) ──

    [Test]
    public async Task ErrorResponse_SerializesAsJson()
    {
        var dto = new ErrorResponse { Error = "something went wrong" };
        var json = JsonSerializer.Serialize(dto);

        await Assert.That(json).Contains("\"error\"");
        await Assert.That(json).Contains("something went wrong");
    }
}
