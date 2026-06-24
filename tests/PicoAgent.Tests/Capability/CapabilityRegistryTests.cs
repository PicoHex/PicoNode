namespace PicoAgent.Tests.Capability;

using PicoAgent;

public class CapabilityRegistryTests
{
    [Test]
    public async Task Scan_DiscoversCapabilities()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-agent-test-{Guid.NewGuid()}");
        var capsDir = Path.Combine(root, "capabilities");
        var pkgDir = Path.Combine(capsDir, "github.com", "test", "my-pack");
        Directory.CreateDirectory(pkgDir);

        var manifestPath = Path.Combine(pkgDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "name": "my-pack",
              "capabilities": [
                {
                  "name": "bash",
                  "handler": "bash tools/runner.sh",
                  "triggerKinds": [0],
                  "triggerToolNames": ["bash"],
                  "lifecycle": 1,
                  "priority": 50
                },
                {
                  "name": "guard",
                  "handler": "node hooks/guard.js",
                  "triggerKinds": [0],
                  "triggerToolNames": [null],
                  "lifecycle": 0,
                  "priority": 0
                }
              ]
            }
            """);

        try
        {
            var registry = new CapabilityRegistry();
            registry.Scan(root);

            var all = registry.GetAll();
            await Assert.That(all.Count).IsEqualTo(2);

            var toolCaps = registry.GetByTrigger(TriggerKind.OnToolCall);
            await Assert.That(toolCaps.Count).IsEqualTo(2);

            var persistent = registry.GetByLifecycle(LifecycleKind.Persistent);
            await Assert.That(persistent.Count).IsEqualTo(1);
            await Assert.That(persistent[0].Name).IsEqualTo("guard");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Scan_EmptyDirectory_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pico-agent-empty-{Guid.NewGuid()}");
        var capsDir = Path.Combine(root, "capabilities");
        Directory.CreateDirectory(capsDir);
        try
        {
            var registry = new CapabilityRegistry();
            registry.Scan(root);
            await Assert.That(registry.GetAll().Count).IsEqualTo(0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
