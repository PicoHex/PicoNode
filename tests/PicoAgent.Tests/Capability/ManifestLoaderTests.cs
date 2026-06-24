namespace PicoAgent.Tests.Capability;

using PicoAgent;

public class ManifestLoaderTests
{
    [Test]
    public async Task ForceSerializerRegistration()
    {
        var data = new ManifestData { Name = "test" };
        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(data);
        var restored = PicoJetson.JsonSerializer.Deserialize<ManifestData>(json);
        await Assert.That(restored!.Name).IsEqualTo("test");
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
                  "triggers": [
                    { "kind": 0, "toolName": "bash" }
                  ],
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
            await Assert.That(manifest.Capabilities[0].Handler)
                .IsEqualTo("bash tools/runner.sh");
            await Assert.That(manifest.Capabilities[0].Triggers[0].Kind)
                .IsEqualTo(TriggerKind.OnToolCall);
            await Assert.That(manifest.Capabilities[0].Triggers[0].ToolName)
                .IsEqualTo("bash");
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
        finally { File.Delete(path); }
    }
}
