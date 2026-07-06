namespace PicoNode.Agent.Tests.Config;

public class AgentPathsTests
{
    [Test]
    public async Task ResolveHomeDir_DataExists_ReturnsLocalData()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico_test_" + Guid.NewGuid().ToString("N")[..8]
        );
        var dataDir = Path.Combine(tmp, "data");
        Directory.CreateDirectory(dataDir);
        try
        {
            var originalCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tmp;
            try
            {
                var result = AgentPaths.ResolveHomeDir();
                await Assert.That(Path.GetFileName(result)).IsEqualTo("data");
                await Assert.That(result).EndsWith(Path.DirectorySeparatorChar + "data");
            }
            finally
            {
                Environment.CurrentDirectory = originalCwd;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            { /* best-effort cleanup */
            }
        }
    }

    [Test]
    public async Task ResolveHomeDir_NoData_ReturnsDefaultHome()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico_test_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        try
        {
            var originalCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tmp;
            try
            {
                var result = AgentPaths.ResolveHomeDir();
                var expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".pico-agent"
                );
                await Assert.That(result).IsEqualTo(expected);
            }
            finally
            {
                Environment.CurrentDirectory = originalCwd;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            { /* best-effort cleanup */
            }
        }
    }
}
