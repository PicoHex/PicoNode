namespace PicoNode.Http.Tests;

public sealed class HeaderValidationTests
{
    [Test]
    public async Task Rejects_pseudo_after_regular()
    {
        var headers = new List<(string, string)>
        {
            ("content-type", "text/plain"),
            (":method", "GET"),
            (":path", "/x"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Rejects_duplicate_pseudo()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":method", "POST"),
            (":path", "/"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Rejects_connection_specific()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/"),
            ("connection", "close"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task Accepts_valid_request()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/"),
            ("user-agent", "test"),
        };
        var result = Http2StreamHandler.ValidateHeadersPublic(headers);
        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Method).IsEqualTo("GET");
        await Assert.That(result.Path).IsEqualTo("/");
    }
}
