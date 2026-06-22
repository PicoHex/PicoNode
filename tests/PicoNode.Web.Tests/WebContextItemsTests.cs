namespace PicoNode.Web.Tests;

public sealed class WebContextItemsTests
{
    [Test]
    public async Task Items_is_lazy_initialized()
    {
        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var items = context.Items;

        await Assert.That(items).IsNotNull();
        await Assert.That(items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Items_supports_add_and_get()
    {
        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        context.Items["key"] = "value";
        var value = context.Items["key"];

        await Assert.That(value).IsEqualTo("value");
    }

    [Test]
    public async Task Items_stores_null_values()
    {
        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        context.Items["key"] = null;

        await Assert.That(context.Items.ContainsKey("key")).IsTrue();
        await Assert.That(context.Items["key"]).IsNull();
    }
}
