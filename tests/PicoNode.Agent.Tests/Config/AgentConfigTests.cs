namespace PicoNode.Agent.Tests.Config;

public class AgentConfigTests
{
    [Test]
    public async Task Deserialize_WithPackages_ParsesList()
    {
        var json =
            """
            {
              "providers": {},
              "packages": [
                "git:github.com/anthropics/skills",
                "local-src/my-tools"
              ]
            }
            """u8;

        var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(json.ToArray());
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Packages).IsNotNull();
        await Assert.That(config.Packages!.Count).IsEqualTo(2);
        await Assert.That(config.Packages[0]).IsEqualTo("git:github.com/anthropics/skills");
        await Assert.That(config.Packages[1]).IsEqualTo("local-src/my-tools");
    }

    [Test]
    public async Task Deserialize_WithoutPackages_PackagesIsNull()
    {
        var json = """{"providers": {}}"""u8;
        var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(json.ToArray());
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Packages).IsNull();
    }

    [Test]
    public async Task Deserialize_EmptyPackages_PackagesIsEmpty()
    {
        var json = """{"providers": {}, "packages": []}"""u8;
        var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(json.ToArray());
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Packages).IsNotNull();
        await Assert.That(config.Packages!.Count).IsEqualTo(0);
    }
}
