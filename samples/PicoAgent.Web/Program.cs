var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
var (server, _) = await PicoAgent.Bootstrap.StartAsync(homeDir, args);
Console.WriteLine($"PicoAgent.Web on http://localhost:{server.Port}");
await Task.Delay(-1);
