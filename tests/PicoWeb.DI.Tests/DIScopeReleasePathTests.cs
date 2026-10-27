namespace PicoWeb.DI.Tests;

/// <summary>
/// Observes <b>both</b> request-scope release paths with a counting probe.
/// <para>
/// <c>WebApp</c> has two ways to end a request scope:
/// </para>
/// <list type="number">
/// <item><b>explicit</b> — <c>await scope.DisposeAsync()</c> in <c>WebApp.Build</c>, on the success path
/// (<c>WebApp.cs:109</c>) and in the <c>catch</c> that rethrows a handler failure (<c>WebApp.cs:90</c>);</item>
/// <item><b>implicit</b> — for a streaming response the scope is handed to <c>ScopeBoundStream</c>, which releases
/// it when the body ends (EOF) or when the stream itself is disposed (a request that never reads the body).</item>
/// </list>
/// <para>
/// Both paths look identical from the client ("200, body received"), and both end with "the probe was disposed
/// once", so the only way to know <em>which</em> one ran — and whether it ran at all — is to count disposals per
/// scope and to record, at release time, whether the body had reported EOF and whether the body stream had already
/// been disposed. Counts are asserted to be equal <em>and</em> non-zero on every path: a test that asserts
/// <c>0 == 0</c> is exactly the hole this file closes.
/// </para>
/// </summary>
public sealed class DIScopeReleasePathTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Buffered_response_releases_the_scope_once(bool useRealContainer)
    {
        await using var run = await ReleaseRun.StartAsync(useRealContainer, streamed: false);

        await Assert.That(run.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(run.BodyBytes).IsGreaterThan(0);

        await Assert.That(run.Ledger.Created).IsEqualTo(2);
        await Assert.That(run.Ledger.Released).IsEqualTo(2);
        await Assert.That(run.Ledger.DisposalsPerScope).IsEqualTo("1,1");
        await Assert.That(run.Ledger.Kinds).IsEqualTo("IAsyncDisposable+IDisposable");

        // No body stream was ever built for a buffered response, so the release cannot have come from
        // ScopeBoundStream: this is the explicit WebApp disposal.
        await Assert.That(run.Ledger.ReleasedInsideFinalBodyRead).IsEqualTo(0);
        await Assert.That(run.Ledger.ReleasedByBodyStreamDisposal).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Streamed_response_releases_the_scope_once_at_body_end(bool useRealContainer)
    {
        await using var run = await ReleaseRun.StartAsync(useRealContainer, streamed: true);

        await Assert.That(run.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(run.BodyBytes).IsGreaterThan(0);

        await Assert.That(run.Ledger.Created).IsEqualTo(2);
        await Assert.That(run.Ledger.DisposalsPerScope).IsEqualTo("1,1");
        await Assert.That(run.Ledger.Kinds).IsEqualTo("IAsyncDisposable+IDisposable");

        // Both probes were released from inside the final body read — the producer had reported EOF and was still
        // undisposed. Deleting that branch inside ScopeBoundStream.ReadAsync leaves the count at 1 (the stream's
        // DisposeAsync releases the scope a moment later, with the producer already disposed), so these two
        // assertions are what make the test able to see it.
        await Assert.That(run.Ledger.ReleasedInsideFinalBodyRead).IsEqualTo(2);
        await Assert.That(run.Ledger.ReleasedByBodyStreamDisposal).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Unread_streamed_response_releases_the_scope_once(bool useRealContainer)
    {
        // HEAD: the pipeline sends the headers and disposes the body stream without ever reading it, so the release
        // can only come from ScopeBoundStream.DisposeAsync.
        await using var run = await ReleaseRun.StartAsync(
            useRealContainer,
            streamed: true,
            head: true
        );

        await Assert.That(run.Status).IsEqualTo(HttpStatusCode.OK);

        // The client's HEAD response completes as soon as the headers arrive; the server releases the scope in the
        // following "finally". Wait for the release so the assertion is about the pipeline, not about scheduling.
        await WaitForReleaseAsync(run.Ledger);

        await Assert.That(run.Ledger.Created).IsEqualTo(2);
        await Assert.That(run.Ledger.DisposalsPerScope).IsEqualTo("1,1");
        await Assert.That(run.Ledger.Kinds).IsEqualTo("IAsyncDisposable+IDisposable");

        // Released by disposing the never-read body stream: the producer is already disposed, no read ever returned 0.
        await Assert.That(run.Ledger.ReleasedByBodyStreamDisposal).IsEqualTo(2);
        await Assert.That(run.Ledger.ReleasedInsideFinalBodyRead).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Explicit_and_implicit_paths_release_the_same_resource_kinds(
        bool useRealContainer
    )
    {
        await using var buffered = await ReleaseRun.StartAsync(useRealContainer, streamed: false);
        await using var streamed = await ReleaseRun.StartAsync(useRealContainer, streamed: true);

        // Non-zero first: "both released nothing" must never read as "both released the same".
        await Assert.That(buffered.Ledger.Released).IsGreaterThan(0);
        await Assert.That(streamed.Ledger.Released).IsGreaterThan(0);

        await Assert.That(streamed.Ledger.Released).IsEqualTo(buffered.Ledger.Released);
        await Assert.That(streamed.Ledger.Kinds).IsEqualTo(buffered.Ledger.Kinds);
        await Assert
            .That(streamed.Ledger.DisposalsPerScope)
            .IsEqualTo(buffered.Ledger.DisposalsPerScope);

        // …and the shared kind string is not the empty string of "released nothing".
        await Assert.That(buffered.Ledger.Kinds).IsEqualTo("IAsyncDisposable+IDisposable");
    }

    private static async Task WaitForReleaseAsync(DisposalLedger ledger)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (ledger.Released < ledger.Created && Environment.TickCount64 < deadline)
            await Task.Delay(10);
    }

    /// <summary>One request through <c>WebApp</c>, with the container/host kept alive until the test has asserted.</summary>
    private sealed class ReleaseRun : IAsyncDisposable
    {
        private readonly ISvcContainer _container;
        private readonly TestWebHost _host;
        private readonly HttpClient _client;

        private ReleaseRun(
            ISvcContainer container,
            TestWebHost host,
            HttpClient client,
            DisposalLedger ledger,
            HttpStatusCode status,
            int bodyBytes
        )
        {
            _container = container;
            _host = host;
            _client = client;
            Ledger = ledger;
            Status = status;
            BodyBytes = bodyBytes;
        }

        public DisposalLedger Ledger { get; }

        public HttpStatusCode Status { get; }

        public int BodyBytes { get; }

        public static async Task<ReleaseRun> StartAsync(
            bool useRealContainer,
            bool streamed,
            bool head = false
        )
        {
            var ledger = new DisposalLedger();
            var eof = new TestFlag();
            var bodyDisposed = new TestFlag();
            ledger.BodyReachedEof = () => eof.Seen;
            ledger.BodyStreamDisposed = () => bodyDisposed.Seen;

            var (container, app) = TestHost.BuildApp(
                useRealContainer,
                c => Probes.RegisterScoped(c, ledger),
                app =>
                    app.MapGet(
                        "/",
                        (ctx, _) =>
                        {
                            Probes.ResolveAll(ctx.Services);

                            if (!streamed)
                                return ValueTask.FromResult(WebResults.Text(200, "buffered"));

                            return ValueTask.FromResult(
                                new HttpResponse
                                {
                                    StatusCode = 200,
                                    Headers =
                                    [
                                        new KeyValuePair<string, string>(
                                            HttpHeaderNames.ContentType,
                                            "text/plain; charset=utf-8"
                                        ),
                                    ],
                                    BodyStream = new EofSignalingStream(
                                        Encoding.UTF8.GetBytes("streamed-body"),
                                        eof,
                                        bodyDisposed
                                    ),
                                }
                            );
                        }
                    )
            );

            var host = await TestWebHost.StartAsync(app);
            var client = host.CreateClient();
            using var request = new HttpRequestMessage(
                head ? HttpMethod.Head : HttpMethod.Get,
                "/"
            );
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();

            return new ReleaseRun(
                container,
                host,
                client,
                ledger,
                response.StatusCode,
                body.Length
            );
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _host.DisposeAsync();
            await _container.DisposeAsync();
        }
    }
}
