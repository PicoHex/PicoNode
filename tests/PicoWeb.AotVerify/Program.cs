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

// Policy gate (spec §4, §5): an unscoped one-token instance tier plus the exempt
// /api/health route, so the native gate observes a real 429 on /api/limited (this
// program mapped only the exempt /api/health before, where the bucket could never be
// exhausted). PerMinute(1, 1) is deliberate: two probes one socket round-trip apart
// must not depend on landing in the same second. Mounted before any auth middleware
// (this program has none) — the design's premise.
using var policy = RateLimitPolicy
    .Create("aot")
    .Exempt("/api/health")
    .Tier("shield", RateLimitBudget.PerMinute(1, 1), RateLimitKeys.Constant("instance"))
    .Build();

app.UseRateLimit(policy);

app.MapGet(
    "/api/health",
    (WebContext ctx, CancellationToken _) =>
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new HealthDto { Status = "ok" });
        return ValueTask.FromResult(Results.Json(200, bytes));
    }
);

// Non-exempt route: the first probe spends the tier's only token, the second must be
// rejected with 429 + Retry-After.
app.MapGet(
    "/api/limited",
    (WebContext ctx, CancellationToken _) =>
        ValueTask.FromResult(WebResults.Text(200, "limited"))
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

// The policy probes run on the same listener/client as the retry loop above (which
// only proves the socket answers): allow → reject → exempt-while-empty.
HttpResponseMessage? limitedAllowed = null;
HttpResponseMessage? limitedRejected = null;
HttpResponseMessage? exemptWhileEmpty = null;
if (response is not null)
{
    limitedAllowed = await client.GetAsync("http://127.0.0.1:" + port + "/api/limited");
    limitedRejected = await client.GetAsync("http://127.0.0.1:" + port + "/api/limited");
    exemptWhileEmpty = await client.GetAsync("http://127.0.0.1:" + port + "/api/health");
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

// ── Policy gate: allow → 429 → exemption ────────────────────────────────
if (limitedAllowed is null || limitedRejected is null || exemptWhileEmpty is null)
{
    Console.WriteLine("FAIL: rate-limit probes did not complete");
    return 1;
}

var limitHeader = "";
if (limitedAllowed.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues))
{
    foreach (var value in limitValues)
    {
        limitHeader = value;
        break;
    }
}

if (limitedAllowed.StatusCode != System.Net.HttpStatusCode.OK || limitHeader.Length == 0)
{
    Console.WriteLine(
        "FAIL: first /api/limited status "
            + limitedAllowed.StatusCode
            + " X-RateLimit-Limit '"
            + limitHeader
            + "'"
    );
    return 1;
}

var retryAfterHeader = "";
if (limitedRejected.Headers.TryGetValues("Retry-After", out var retryAfterValues))
{
    foreach (var value in retryAfterValues)
    {
        retryAfterHeader = value;
        break;
    }
}

if (
    limitedRejected.StatusCode != System.Net.HttpStatusCode.TooManyRequests
    || retryAfterHeader.Length == 0
)
{
    Console.WriteLine(
        "FAIL: second /api/limited status "
            + limitedRejected.StatusCode
            + " Retry-After '"
            + retryAfterHeader
            + "'"
    );
    return 1;
}

if (exemptWhileEmpty.StatusCode != System.Net.HttpStatusCode.OK)
{
    Console.WriteLine("FAIL: exempt /api/health status " + exemptWhileEmpty.StatusCode);
    return 1;
}

Console.WriteLine(
    "rate-limit: /api/limited="
        + limitedAllowed.StatusCode
        + " (X-RateLimit-Limit="
        + limitHeader
        + "), /api/limited again="
        + limitedRejected.StatusCode
        + " (Retry-After="
        + retryAfterHeader
        + "), /api/health while empty="
        + exemptWhileEmpty.StatusCode
);

Console.WriteLine("PASS");
return 0;

public sealed class HealthDto
{
    public string Status { get; set; } = string.Empty;
}
