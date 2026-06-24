namespace PicoAgent.Tests.Capability;

using PicoAgent;
using System.Text.Json;

public class CapabilityRunnerTests
{
    [Test]
    public async Task ExecuteAsync_EchoTool_ReturnsResult()
    {
        var scriptDir = Path.Combine(Path.GetTempPath(), "pico-test-" + Guid.NewGuid());
        Directory.CreateDirectory(scriptDir);

        string scriptPath;
        string handler;
        string scriptContent;

        if (OperatingSystem.IsWindows())
        {
            scriptPath = Path.Combine(scriptDir, "echo.bat");
            // Read first line from stdin, echo a JSON response
            scriptContent = "@echo off\r\nset /p line=\r\necho {\"content\":\"got it\"}\r\n";
            handler = $"cmd.exe /c {scriptPath}";
        }
        else
        {
            scriptPath = Path.Combine(scriptDir, "echo.sh");
            scriptContent = "#!/bin/bash\nread line\necho '{\"content\":\"got it\"}'\n";
            handler = scriptPath;
        }

        await File.WriteAllTextAsync(scriptPath, scriptContent);

        if (!OperatingSystem.IsWindows())
            Process.Start("chmod", $"+x {scriptPath}")?.WaitForExit();

        try
        {
            var config = new ManifestCapability
            {
                Name = "echo",
                Handler = handler,
                Lifecycle = 1,
            };

            var runner = new CapabilityRunner();
            var inputJson = """{"kind":"tool_call","toolCallId":"1","toolName":"echo","args":{}}"""u8.ToArray();

            var result = await runner.ExecuteAsync(
                config, "tool_call", inputJson, CancellationToken.None);

            await Assert.That(result.TryGetProperty("content", out var c)).IsTrue();
            await Assert.That(c.GetString()).IsEqualTo("got it");
        }
        finally
        {
            if (Directory.Exists(scriptDir)) Directory.Delete(scriptDir, true);
        }
    }
}
