namespace PicoNode.Agent.Tests.Capability;

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
            var inputJson =
                """{"kind":"tool_call","toolCallId":"1","toolName":"echo","args":{}}"""u8.ToArray();
            var result = await runner.ExecuteAsync(
                config,
                "tool_call",
                inputJson,
                CancellationToken.None
            );

            await Assert.That(result.TryGetProperty("content", out var c)).IsTrue();
            await Assert.That(c.GetString()).IsEqualTo("got it");
        }
        finally
        {
            if (Directory.Exists(scriptDir))
                Directory.Delete(scriptDir, true);
        }
    }

    [Test]
    public async Task ValidateArgs_MissingRequired_ThrowsSchemaError()
    {
        var config = new ManifestCapability
        {
            Name = "test",
            Handler = "echo",
            Lifecycle = 1,
            Schema =
                """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""",
        };
        var args = new Dictionary<string, object>();
        var ex = Assert.Throws<ToolException>(() => CapabilityRunner.ValidateArgs(config, args));
        await Assert.That(ex!.Code).IsEqualTo(ToolErrorCode.SchemaValidationFailed);
    }

    [Test]
    public async Task TruncateOutput_ExceedsLimit_ShouldAppendNotice()
    {
        var longOutput = new string('x', 60 * 1024);
        var result = CapabilityRunner.TruncateOutput(longOutput, 50 * 1024, 2000);
        await Assert.That(result.Content.Length).IsLessThanOrEqualTo(55 * 1024);
        await Assert.That(result.Content).Contains("Output truncated");
        await Assert.That(result.FullOutputPath).IsNotNull();
    }
}
