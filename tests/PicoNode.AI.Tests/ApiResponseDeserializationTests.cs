
namespace PicoNode.AI.Tests;

public class ApiResponseDeserializationTests
{
    [Test]
    public async Task Deserialize_OpenAiError_ReturnsMessage()
    {
        var json = """{"error":{"message":"Incorrect API key","type":"invalid_request_error"}}""";
        var err = PicoJetson.JsonSerializer.Deserialize<OpenAiErrorResponse>(
            Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(err).IsNotNull();
        await Assert.That(err!.Error).IsNotNull();
        await Assert.That(err.Error!.Message).IsEqualTo("Incorrect API key");
    }

    [Test]
    public async Task Deserialize_AnthropicError_ReturnsMessage()
    {
        var json = """{"error":{"type":"rate_limit_error","message":"Rate limited"}}""";
        var err = PicoJetson.JsonSerializer.Deserialize<AnthropicErrorResponse>(
            Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(err).IsNotNull();
        await Assert.That(err!.Error).IsNotNull();
        await Assert.That(err.Error!.Message).IsEqualTo("Rate limited");
    }

    [Test]
    public async Task Deserialize_ModelList_ReturnsModels()
    {
        var json =
            """{"data":[{"id":"gpt-4","owned_by":"openai"},{"id":"gpt-3.5","owned_by":"openai"}]}""";
        var list = PicoJetson.JsonSerializer.Deserialize<ModelListResponse>(
            Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(list).IsNotNull();
        await Assert.That(list!.Data.Length).IsEqualTo(2);
        await Assert.That(list.Data[0].Id).IsEqualTo("gpt-4");
        await Assert.That(list.Data[1].OwnedBy).IsEqualTo("openai");
    }
}
