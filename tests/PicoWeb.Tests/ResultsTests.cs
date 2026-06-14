namespace PicoWeb.Tests;

public sealed class ResultsTests
{
    private static readonly byte[] SampleJson = """
        {"name":"Alice"}
        """u8.ToArray();

    [Test]
    public async Task Json_returns_serialized_body()
    {
        var response = Results.Json(200, SampleJson);
        var body = System.Text.Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers.TryGetValue("Content-Type", out var ct)).IsTrue();
        await Assert.That(ct).IsEqualTo("application/json; charset=utf-8");
        await Assert.That(body).Contains("Alice");
    }

    [Test]
    public async Task Text_returns_text_response()
    {
        var response = Results.Text(200, "hello");
        var body = System.Text.Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(body).IsEqualTo("hello");
    }

    [Test]
    public async Task Empty_returns_status_only()
    {
        var response = Results.Empty(204);
        await Assert.That(response.StatusCode).IsEqualTo(204);
        await Assert.That(response.Body.IsEmpty).IsTrue();
    }
}
