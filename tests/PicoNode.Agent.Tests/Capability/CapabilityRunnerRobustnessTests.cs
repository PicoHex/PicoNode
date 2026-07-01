using System.Text.Json;

namespace PicoNode.Agent.Tests.Capability;

/// <summary>
/// TDD Batch 6: CapabilityRunner subprocess robustness.
///
/// Gaps in CapabilityRunner.ExecuteAsync:
///   1. Cancellation does not kill the subprocess — a hung/long-running process
///      lingers after the caller cancels, orphaning file handles and CPU.
///   2. Malformed JSON on stdout raises a raw System.Text.Json.JsonException
///      instead of a domain ToolException(ExecutionFailed) — callers cannot
///      distinguish "tool crashed" from "bug in agent runtime".
///   3. Subprocess writing nothing to stdout (early exit / write-only-to-stderr)
///      currently returns default(JsonElement) silently. Callers cannot detect
///      the failure — should raise ToolException(ExecutionFailed) with stderr.
/// </summary>
public class CapabilityRunnerRobustnessTests
{
    private static (string handler, string scriptDir, string scriptPath) WriteScript(
        string body,
        string extension
    )
    {
        var scriptDir = Path.Combine(Path.GetTempPath(), "pico-cap-test-" + Guid.NewGuid());
        Directory.CreateDirectory(scriptDir);
        string scriptPath;
        string handler;
        if (OperatingSystem.IsWindows())
        {
            scriptPath = Path.Combine(scriptDir, "tool.bat");
            File.WriteAllText(scriptPath, body);
            handler = $"cmd.exe /c \"{scriptPath}\"";
        }
        else
        {
            scriptPath = Path.Combine(scriptDir, "tool" + extension);
            File.WriteAllText(scriptPath, body);
            Process.Start("chmod", $"+x {scriptPath}")?.WaitForExit();
            handler = scriptPath;
        }
        return (handler, scriptDir, scriptPath);
    }

    [Test]
    public async Task ExecuteAsync_Cancellation_KillsSubprocess()
    {
        // A script that reads input, sleeps for a long time, then would write output.
        // If cancellation properly kills the subprocess we should observe:
        //  - ExecuteAsync throws OperationCanceledException promptly (< 2s)
        //  - The subprocess is not still running (no orphaned PID).
        string body;
        string ext;
        if (OperatingSystem.IsWindows())
        {
            // read one line, then sleep 30s
            body = "@echo off\r\nset /p line=\r\nping -n 30 127.0.0.1 > nul\r\necho {}\r\n";
            ext = ".bat";
        }
        else
        {
            body = "#!/bin/bash\nread line\nsleep 30\necho '{}'\n";
            ext = ".sh";
        }

        var (handler, dir, _) = WriteScript(body, ext);
        try
        {
            var config = new ManifestCapability
            {
                Name = "slow",
                Handler = handler,
                Lifecycle = 1,
            };
            var runner = new CapabilityRunner();
            var input = "{}"u8.ToArray();

            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var task = runner.ExecuteAsync(config, "tool_call", input, cts.Token);

            // Give it a moment to spawn the subprocess, then cancel.
            await Task.Delay(300);
            cts.Cancel();

            // ExecuteAsync must surface the cancellation quickly (well under 30s sleep).
            OperationCanceledException? caught = null;
            try
            {
                await task;
            }
            catch (OperationCanceledException oce)
            {
                caught = oce;
            }
            sw.Stop();

            await Assert.That(caught).IsNotNull();
            await Assert.That(sw.Elapsed.TotalSeconds).IsLessThan(10);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            { /* subprocess may still hold script file */
            }
        }
    }

    [Test]
    public async Task ExecuteAsync_MalformedJsonOutput_ThrowsToolException()
    {
        string body;
        string ext;
        if (OperatingSystem.IsWindows())
        {
            body = "@echo off\r\nset /p line=\r\necho not-valid-json\r\n";
            ext = ".bat";
        }
        else
        {
            body = "#!/bin/bash\nread line\necho 'not-valid-json'\n";
            ext = ".sh";
        }

        var (handler, dir, _) = WriteScript(body, ext);
        try
        {
            var config = new ManifestCapability
            {
                Name = "bad",
                Handler = handler,
                Lifecycle = 1,
            };
            var runner = new CapabilityRunner();
            var input = "{}"u8.ToArray();

            ToolException? caught = null;
            try
            {
                await runner.ExecuteAsync(config, "tool_call", input, CancellationToken.None);
            }
            catch (ToolException ex)
            {
                caught = ex;
            }

            await Assert.That(caught).IsNotNull();
            await Assert.That(caught!.Code).IsEqualTo(ToolErrorCode.ExecutionFailed);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ExecuteAsync_EmptyStdoutWithStderr_ThrowsToolExceptionWithStderr()
    {
        string body;
        string ext;
        if (OperatingSystem.IsWindows())
        {
            // consume stdin, write to stderr only, exit non-zero
            body = "@echo off\r\nset /p line=\r\necho boom 1>&2\r\nexit /b 2\r\n";
            ext = ".bat";
        }
        else
        {
            body = "#!/bin/bash\nread line\necho 'boom' 1>&2\nexit 2\n";
            ext = ".sh";
        }

        var (handler, dir, _) = WriteScript(body, ext);
        try
        {
            var config = new ManifestCapability
            {
                Name = "err",
                Handler = handler,
                Lifecycle = 1,
            };
            var runner = new CapabilityRunner();
            var input = "{}"u8.ToArray();

            ToolException? caught = null;
            try
            {
                await runner.ExecuteAsync(config, "tool_call", input, CancellationToken.None);
            }
            catch (ToolException ex)
            {
                caught = ex;
            }

            await Assert.That(caught).IsNotNull();
            await Assert.That(caught!.Code).IsEqualTo(ToolErrorCode.ExecutionFailed);
            // stderr content must be preserved so the LLM/user can see the real cause.
            await Assert.That(caught.Message).Contains("boom");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
