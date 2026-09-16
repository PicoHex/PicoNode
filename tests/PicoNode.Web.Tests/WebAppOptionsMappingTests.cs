using PicoNode.Http;
using PicoNode.Web;

namespace PicoNode.Web.Tests;

public sealed class WebAppOptionsMappingTests
{
    [Test]
    public async Task ToHttpConnectionHandlerOptions_maps_all_properties()
    {
        var options = new WebAppOptions
        {
            ServerHeader = "Test-Server",
            MaxRequestBytes = 16384,
            StreamingResponseBufferSize = 8192,
            RequestTimeout = TimeSpan.FromSeconds(10),
            Logger = null,
        };

        var handler = options.ToHttpConnectionHandlerOptions(
            (request, ct) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );

        await Assert.That(handler.RequestHandler is not null).IsTrue();
        await Assert.That(handler.ServerHeader).IsEqualTo("Test-Server");
        await Assert.That(handler.MaxRequestBytes).IsEqualTo(16384);
        await Assert.That(handler.StreamingResponseBufferSize).IsEqualTo(8192);
        await Assert.That(handler.RequestTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(handler.WebSocketMessageHandler is null).IsTrue();
        await Assert.That(handler.Logger).IsNull();
    }

    [Test]
    public async Task ToHttpConnectionHandlerOptions_maps_websocket_max_message_size()
    {
        var options = new WebAppOptions { WebSocketMaxMessageSize = 8 * 1024 * 1024 };

        var handler = options.ToHttpConnectionHandlerOptions(
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );

        await Assert.That(handler.WebSocketMaxMessageSize).IsEqualTo(8 * 1024 * 1024);
    }

    [Test]
    public async Task WebAppOptions_websocket_max_message_size_defaults_to_256KB()
    {
        var options = new WebAppOptions();

        await Assert.That(options.WebSocketMaxMessageSize).IsEqualTo(256 * 1024);
    }
}
