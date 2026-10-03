namespace PicoNode.Web.Tests;

/// <summary>
/// Task 8 — <c>UseRateLimit</c> mounting (spec §3.4): policy registration and duplicate
/// rejection, outer-to-inner registration order, the legacy+policy group-level "first
/// writer wins" rule (B5, spec §4) and the unscoped shield tier's path range (B2, spec §3.4).
/// The mounting order is only observable through the real <see cref="WebApp"/> pipeline, so
/// these tests drive <see cref="WebApp.Build"/> with raw HTTP/1.1 requests.
/// </summary>
public sealed class UseRateLimitTests
{
    [Test]
    public async Task UseRateLimit_Returns_The_App_And_Registers()
    {
        var app = new WebApp(new TestContainer());
        using var policy = P("web", RateLimitBudget.PerMinute(5, 5));

        var returned = app.UseRateLimit(policy);

        await Assert.That(ReferenceEquals(returned, app)).IsTrue();
        await Assert.That(app.RateLimits.Contains("web")).IsTrue();
        await Assert.That(app.RateLimits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UseRateLimit_Throws_On_Duplicate_Policy_Name()
    {
        var app = new WebApp(new TestContainer());
        using var first = P("web", RateLimitBudget.PerMinute(5, 5));
        using var second = P("web", RateLimitBudget.PerMinute(5, 5));

        app.UseRateLimit(first);

        await Assert
            .That(() => app.UseRateLimit(second))
            .Throws<InvalidOperationException>()
            .Because("a duplicate policy name makes observation and future mounting ambiguous");
        await Assert.That(app.RateLimits.Count).IsEqualTo(1);

        // Names are Ordinal: a different name is accepted and registered.
        using var third = P("api", RateLimitBudget.PerMinute(5, 5));
        app.UseRateLimit(third);
        await Assert.That(app.RateLimits.Count).IsEqualTo(2);
        await Assert.That(app.RateLimits.Contains("api")).IsTrue();
    }

    [Test]
    public async Task UseRateLimit_Registers_In_Order_Outer_To_Inner()
    {
        // First registered = outermost. Both policies match and allow, so the group-level
        // "first writer wins" rule (spec §3.2.5) decides which headers survive: the
        // innermost lambda writes first on the response unwind, so the *last registered*
        // policy owns the X-RateLimit-* group. A reversed chain (UseRateLimit prepending)
        // would expose the first registered policy's group instead (5/4, not 9/8).
        using var first = RateLimitPolicy.Create("first")
            .Tier("first-tier", RateLimitBudget.PerMinute(5, 5), RateLimitKeys.Constant("instance"))
            .Build();
        using var second = RateLimitPolicy.Create("second")
            .Tier(
                "second-tier",
                RateLimitBudget.PerMinute(9, 9),
                RateLimitKeys.Constant("instance")
            )
            .Build();

        var app = new WebApp(new TestContainer());
        app.UseRateLimit(first);
        app.UseRateLimit(second);
        app.MapGet(
            "/x",
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );

        var response = await SendAsync(app.Build(), "/x");

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Header("X-RateLimit-Limit")).IsEqualTo("9");
        await Assert.That(response.Header("X-RateLimit-Remaining")).IsEqualTo("8");
        await Assert.That(response.HeaderCount("X-RateLimit-Limit")).IsEqualTo(1);

        // Both stayed in the chain and both consumed exactly one token: the outer policy
        // ran first (else the inner 429/next would have skipped it).
        await Assert.That((await first.Stores[0].TryConsumeTokenAsync("instance")).Remaining).IsEqualTo(3);
        await Assert.That((await second.Stores[0].TryConsumeTokenAsync("instance")).Remaining).IsEqualTo(7);
    }

    [Test]
    public async Task Legacy_And_Policy_Stacked_Keep_The_First_Writers_Headers()
    {
        // B5 / spec §4: co-installed legacy single-bucket middleware and a policy must not
        // duplicate the X-RateLimit-* group. The inner (policy) writes first on the response
        // unwind; the outer legacy middleware's group-level guard then writes none of it, so
        // the wire carries one copy of the policy's values (never the legacy MaxTokens).
        var legacyOptions = new RateLimitOptions
        {
            MaxTokens = 7,
            RefillRate = 0,
            RefillInterval = TimeSpan.FromSeconds(1),
            KeySelector = static _ => "legacy",
        };
        using var legacyStore = new InMemoryRateLimitStore(legacyOptions);
        using var policy = P("web", RateLimitBudget.PerMinute(3, 3));

        var app = new WebApp(new TestContainer());
        app.Use(RateLimitMiddleware.Create(legacyStore, legacyOptions));
        app.UseRateLimit(policy);
        app.MapGet(
            "/x",
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );

        var response = await SendAsync(app.Build(), "/x");

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Header("X-RateLimit-Limit")).IsEqualTo("3");
        await Assert.That(response.Header("X-RateLimit-Remaining")).IsEqualTo("2");
        await Assert.That(response.HeaderCount("X-RateLimit-Limit")).IsEqualTo(1);
        await Assert.That(response.HeaderCount("X-RateLimit-Remaining")).IsEqualTo(1);
        await Assert.That(response.HeaderCount("X-RateLimit-Reset")).IsEqualTo(1);
        await Assert
            .That(response.Raw.Contains("X-RateLimit-Limit: 7"))
            .IsFalse()
            .Because("the outer legacy middleware must yield to the first writer, not append");

        // The legacy middleware really ran and consumed its own token (7 -> 6); the extra
        // consume observes 5. A skipped legacy layer would leave 6.
        await Assert.That((await legacyStore.TryConsumeTokenAsync("legacy")).Remaining).IsEqualTo(5);
    }

    [Test]
    public async Task B2_Acp_And_Unknown_Paths_Share_The_Shield_Tier()
    {
        // B2 / spec §3.4: the shield tier's path range equals AuthMiddleware's (global), so
        // one *unscoped* tier must cover /acp, unknown paths and /api/... alike. The
        // Constant("instance") key is one bucket for every path; only the explicit
        // Exempt("/api/health") escapes. A path-scoped shield (Prefix("/api") on the tier)
        // would leave /acp and /nope unlimited and pass this test's rejections as 404/200.
        // PerMinute(1, 1) — not PerSecond — so three sequential requests cannot outlive the
        // window on a loaded runner (proportional refill would then answer 200).
        using var policy = RateLimitPolicy.Create("web")
            .Exempt("/api/health")
            .Tier("instance", RateLimitBudget.PerMinute(1, 1), RateLimitKeys.Constant("instance"))
            .Build();

        var app = new WebApp(new TestContainer());
        app.UseRateLimit(policy);
        app.MapGet(
            "/acp",
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );
        app.MapGet(
            "/api/x",
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );
        app.MapGet(
            "/api/health",
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 })
        );
        var handler = app.Build();

        // First request spends the single shield token.
        var acp = await SendAsync(handler, "/acp");
        await Assert.That(acp.Status).IsEqualTo(200);
        await Assert.That(acp.Header("X-RateLimit-Limit")).IsEqualTo("1");

        // An unknown path is still inside the shield (429, not the router's 404)...
        var unknown = await SendAsync(handler, "/nope");
        await Assert
            .That(unknown.Status)
            .IsEqualTo(429)
            .Because("the shield must cover unknown paths too — it is not scoped to /api");

        // ...and so is /api/...: all three share the "instance" bucket.
        var api = await SendAsync(handler, "/api/x");
        await Assert.That(api.Status).IsEqualTo(429);

        // The one explicit exemption passes without touching the bucket.
        var health = await SendAsync(handler, "/api/health");
        await Assert.That(health.Status).IsEqualTo(200);
        await Assert.That(health.Header("X-RateLimit-Limit")).IsNull();
        await Assert.That(health.Header("X-RateLimit-Remaining")).IsNull();
    }

    private static RateLimitPolicy P(string name, RateLimitBudget budget) =>
        RateLimitPolicy.Create(name)
            .Tier("instance", budget, RateLimitKeys.Constant("instance"))
            .Build();

    private static async Task<RawResponse> SendAsync(ITcpConnectionHandler handler, string target)
    {
        var connection = new RecordingConnection();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );
        await handler.OnReceivedAsync(connection, request, CancellationToken.None);

        var text = new StringBuilder();
        for (var i = 0; i < connection.Sent.Count; i++)
            text.Append(Encoding.ASCII.GetString(connection.Sent[i]));

        return RawResponse.Parse(text.ToString());
    }

    /// <summary>Parsed raw HTTP/1.1 response: status line plus the header values in order.</summary>
    private sealed class RawResponse
    {
        private readonly Dictionary<string, List<string>> _headers;

        private RawResponse(int status, Dictionary<string, List<string>> headers, string raw)
        {
            Status = status;
            _headers = headers;
            Raw = raw;
        }

        public int Status { get; }

        public string Raw { get; }

        public string? Header(string name) =>
            _headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

        public int HeaderCount(string name) =>
            _headers.TryGetValue(name, out var values) ? values.Count : 0;

        public static RawResponse Parse(string raw)
        {
            var headEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = headEnd >= 0 ? raw[..headEnd] : raw;
            var lines = head.Split("\r\n", StringSplitOptions.None);

            var statusLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var status = int.Parse(statusLine[1], CultureInfo.InvariantCulture);

            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    continue;

                var name = lines[i][..colon].Trim();
                var value = lines[i][(colon + 1)..].Trim();
                if (!headers.TryGetValue(name, out var values))
                    headers[name] = values = [];
                values.Add(value);
            }

            return new RawResponse(status, headers, raw);
        }
    }

    private sealed class RecordingConnection : ITcpConnectionContext
    {
        public long ConnectionId { get; init; } = 1;

        public EndPoint RemoteEndPoint { get; init; } = new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public CancellationToken RemoteCloseToken => CancellationToken.None;

        public string? NegotiatedProtocol => null;

        public List<byte[]> Sent { get; } = [];

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public void Close() { }
    }
}
