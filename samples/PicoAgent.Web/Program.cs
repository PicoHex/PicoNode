// PicoAgent.Web — thin frontend host on port 9000 with static files.
// Config, agent lifecycle, and API endpoints are delegated to PicoAgent.Bootstrap.

var (server, system) = await PicoAgent.Bootstrap.CreateAsync();
await server.ListenAsync("http://localhost:9000");
Console.WriteLine("PicoAgent.Web on http://localhost:9000");
await Task.Delay(-1);
