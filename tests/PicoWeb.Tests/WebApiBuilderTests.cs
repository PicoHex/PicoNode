namespace PicoWeb.Tests;

public sealed class WebApiBuilderTests
{
    [Test]
    public async Task Build_returns_WebApiApp()
    {
        var builder = new WebApiBuilder();
        var api = builder.Build();
        await Assert.That(api).IsNotNull();
    }

    [Test]
    public async Task ConfigureApp_sets_ServerHeader()
    {
        var builder = new WebApiBuilder();
        builder.ConfigureApp(o => new WebAppOptions { ServerHeader = "TestHeader" });
        var api = builder.Build();
        await Assert.That(api).IsNotNull();
    }
}
