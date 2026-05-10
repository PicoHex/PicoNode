using PicoCfg.Extensions;

namespace PicoNode.Tests;

public sealed class CfgBindSmokeTest
{
    [Test]
    public async Task ConfigBinding_WebAppOptions_ReadsValuesFromCfg()
    {
        await using var root = await Cfg
            .CreateBuilder()
            .Add(new Dictionary<string, string>
            {
                ["WebApp:ServerHeader"] = "TestServer/1.0",
                ["WebApp:MaxRequestBytes"] = "16384",
                ["WebApp:StreamingResponseBufferSize"] = "8192",
                ["WebApp:RequestTimeout"] = "00:00:45",
            })
            .BuildAsync();

        await Assert.That(root.GetValue("WebApp:ServerHeader")).IsEqualTo("TestServer/1.0");
        await Assert.That(root.GetValue("WebApp:MaxRequestBytes")).IsEqualTo("16384");
        await Assert.That(root.GetValue("WebApp:StreamingResponseBufferSize")).IsEqualTo("8192");
        await Assert.That(root.GetValue("WebApp:RequestTimeout")).IsEqualTo("00:00:45");
    }

    [Test]
    public async Task CfgBind_Bind_WorksWithSimpleType()
    {
        await using var root = await Cfg
            .CreateBuilder()
            .Add(new Dictionary<string, string>
            {
                ["App:Name"] = "PicoCfg",
                ["App:Enabled"] = "true",
                ["App:Count"] = "42",
            })
            .BuildAsync();

        var settings = CfgBind.Bind<AppSettings>(root, "App");

        await Assert.That(settings.Name).IsEqualTo("PicoCfg");
        await Assert.That(settings.Enabled).IsTrue();
        await Assert.That(settings.Count).IsEqualTo(42);
    }

    public sealed class AppSettings
    {
        public string? Name { get; set; }
        public bool Enabled { get; set; }
        public int Count { get; set; }
    }
}
