// PicoAgent.Cli — command-line host.
// Config, agent lifecycle, and API endpoints are delegated to PicoAgent.Bootstrap.

var (server, _) = await PicoAgent.Bootstrap.StartAsync(args);
Console.WriteLine($"PicoAgent listening on http://localhost:{server.Port}");
await Task.Delay(-1);
