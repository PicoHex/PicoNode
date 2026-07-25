using PicoJetson;
using PicoNode.Http;

namespace PicoWeb.Tests;

[PicoJsonSerializable]
public sealed class TestDto
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public sealed class JsonResultTests
{
    [Test]
    public async Task JsonResult_SerializesAndReturnsJson()
    {
        var ctx = WebContext.Create(new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Path = "/",
        });
        var dto = new TestDto { Name = "Alice", Age = 30 };
        var result = new JsonResult<TestDto>(dto);

        var response = result.Execute(ctx);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers).Contains(
            new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8"));
        await Assert.That(response.Body.IsEmpty).IsFalse();

        var json = Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(json).Contains("Alice");
        await Assert.That(json).Contains("30");
    }
}
