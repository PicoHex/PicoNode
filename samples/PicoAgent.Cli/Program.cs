using PicoNode.Agent.Domain;

var home = new HomeDir(HomeDir.Resolve());
home.EnsureCreated();

var (server, _) = await PicoAgent.Bootstrap.StartAsync(args);
Console.WriteLine($"Listening on http://localhost:{server.Port}");
await Task.Delay(-1);
