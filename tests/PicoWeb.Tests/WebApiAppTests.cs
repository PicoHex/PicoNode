using PicoNode.Http;

namespace PicoWeb.Tests;

public sealed class WebApiAppTests
{
    [Test]
    public async Task MapGet_registers_handler_in_WebApp()
    {
        var builder = new WebApiBuilder();
        var api = builder.Build();

        var invoked = false;
        api.MapGet("/hello", (WebContext ctx) =>
        {
            invoked = true;
            return ValueTask.FromResult(Results.Text(200, "ok"));
        });

        var handler = api.GetHandler();
        var context = new RecordingConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        await handler.OnReceivedAsync(context, request, CancellationToken.None);
        await Assert.That(invoked).IsTrue();
    }
}

internal sealed class RecordingConnectionContext : ITcpConnectionContext
{
    public long ConnectionId => 1;
    public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 9999);
    public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityUtc => DateTimeOffset.UtcNow;
    public object? UserState { get; set; }
    public string? NegotiatedProtocol => null;
    public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default) => Task.CompletedTask;
    public void Close() { }
}
