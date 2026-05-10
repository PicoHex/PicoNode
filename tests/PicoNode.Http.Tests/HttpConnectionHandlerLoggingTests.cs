namespace PicoNode.Http.Tests;

public sealed class HttpConnectionHandlerLoggingTests
{
    [Test]
    public async Task Options_has_Logger_property_of_type_ILogger_nullable()
    {
        var prop = typeof(HttpConnectionHandlerOptions).GetProperty(
            "Logger",
            BindingFlags.Public | BindingFlags.Instance
        );

        await Assert.That(prop).IsNotNull();
        await Assert.That(prop!.PropertyType).IsEqualTo(typeof(ILogger));
        await Assert.That(prop.CanRead).IsTrue();
        await Assert.That(prop.CanWrite).IsTrue();
    }

    [Test]
    public async Task Options_Logger_defaults_to_null()
    {
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                ),
        };

        await Assert.That(options.Logger).IsNull();
    }
}
