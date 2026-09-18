using PicoJetson;
using PicoNode.Web;
using PicoWeb;

// AOT publish-and-run verification program, driven by
// scripts/test-aot-publish.ps1. It is intentionally a committed project inside
// the repository: a generated project outside the tree made MSBuild resolve the
// referenced projects' own relative references against the wrong base on
// macOS/Linux (MSB3202), while committed projects publish AOT reliably on every
// CI runner.
//
// Exit code is the verdict; the script propagates it.

var api = new WebApiBuilder()
    .ConfigureApp(_ => new WebAppOptions { ServerHeader = "AotVerify" })
    .Build();
var app = api.App;

app.MapGet(
    "/api/health",
    (WebContext ctx, CancellationToken _) =>
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new HealthDto { Status = "ok" });
        return ValueTask.FromResult(Results.Json(200, bytes));
    }
);

// Port 0: the OS assigns a free port, so a second concurrent run (or an
// unrelated service) cannot collide with this gate.
await using var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0) }
);
await server.StartAsync();
var port = ((System.Net.IPEndPoint)server.LocalEndPoint!).Port;

// Probe the listener (retry instead of a fixed sleep).
using var client = new HttpClient();
HttpResponseMessage? response = null;
string? body = null;
for (var attempt = 0; attempt < 25; attempt++)
{
    try
    {
        response = await client.GetAsync("http://127.0.0.1:" + port + "/api/health");
        body = await response.Content.ReadAsStringAsync();
        break;
    }
    catch (HttpRequestException)
    {
        await Task.Delay(200);
    }
}

// Graceful stop before disposal (DisposeAsync then no-ops) so the probe result
// is final before the process tears the server down.
await server.StopAsync();

// Also exercise the documented api.RunAsync(...) entry point under AOT: port 0
// keeps the OS-assigned-port guarantee, and cancellation drives ProcessShutdown.
using (var runCts = new CancellationTokenSource())
{
    var runTask = api.RunAsync("http://127.0.0.1:0", runCts.Token);
    await Task.Delay(200);
    await runCts.CancelAsync();
    await runTask;
}

if (response is null || body is null)
{
    Console.WriteLine("FAIL: server did not answer");
    return 1;
}

if (response.StatusCode != System.Net.HttpStatusCode.OK)
{
    Console.WriteLine("FAIL: status " + response.StatusCode);
    return 1;
}

if (!body.Contains("ok"))
{
    Console.WriteLine("FAIL: unexpected body '" + body + "'");
    return 1;
}

Console.WriteLine("PASS");
return 0;

public sealed class HealthDto
{
    public string Status { get; set; } = string.Empty;
}
