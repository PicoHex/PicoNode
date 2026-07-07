using PicoNode.Agent.Domain;

var homeDir = ResolveHomeDir();
Directory.CreateDirectory(homeDir);

var (server, _) = await PicoAgent.Bootstrap.StartAsync(homeDir, args);
Console.WriteLine($"Listening on http://localhost:{server.Port}");
await Task.Delay(-1);

static string ResolveHomeDir()
{
    var portable = Path.Combine(Directory.GetCurrentDirectory(), "data");
    if (Directory.Exists(portable)) return Path.GetFullPath(portable);
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pico-agent");
}
