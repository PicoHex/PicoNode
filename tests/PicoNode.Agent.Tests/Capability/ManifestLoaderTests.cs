namespace PicoNode.Agent.Tests.Capability;

public class ManifestLoaderTests
{
    [Test]
    public async Task SgInit_ForceRegistration()
    {
        var d = new ManifestData { Name = "x" };
        var j = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(d);
        var r = PicoJetson.JsonSerializer.Deserialize<ManifestData>(j);
        await Assert.That(r!.Name).IsEqualTo("x");
    }

    [Test]
    public async Task Load_ValidManifest_ReturnsCapabilities()
    {
        var json = """
            {
              "name": "test-pack",
              "version": "1.0.0",
              "capabilities": [
                {
                  "name": "bash",
                  "handler": "bash tools/runner.sh",
                  "triggerKinds": [0],
                  "triggerToolNames": ["bash"],
                  "lifecycle": 1,
                  "priority": 50,
                  "description": "Run shell commands"
                }
              ]
            }
            """;

        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var manifest = ManifestLoader.LoadFromFile(path);

            await Assert.That(manifest).IsNotNull();
            await Assert.That(manifest!.Name).IsEqualTo("test-pack");
            await Assert.That(manifest.Capabilities.Length).IsEqualTo(1);
            await Assert.That(manifest.Capabilities[0].Name).IsEqualTo("bash");
            await Assert.That(manifest.Capabilities[0].Handler).IsEqualTo("bash tools/runner.sh");
            await Assert.That(manifest.Capabilities[0].TriggerKinds[0]).IsEqualTo(0);
            await Assert.That(manifest.Capabilities[0].TriggerToolNames[0]).IsEqualTo("bash");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_EmptyCapabilities_ReturnsEmptyList()
    {
        var json = """{"name":"minimal","capabilities":[]}""";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var manifest = ManifestLoader.LoadFromFile(path);
            await Assert.That(manifest!.Capabilities.Length).IsEqualTo(0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
