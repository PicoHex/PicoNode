using PicoNode.Http;
using PicoNode.Web;

namespace PicoNode.PerfHarness;

/// <summary>
/// Task 9 — records the per-request allocation of the constant-key, single-tier
/// <b>allow</b> path through
/// <see cref="RateLimitMiddleware.Create(RateLimitPolicy)"/> (spec §3.5, §4, §8).
/// </summary>
/// <remarks>
/// The number is <b>recorded</b>, not promised: it is printed and guarded only by a
/// loose tripwire against pathological regressions, never asserted as an exact value
/// (allocation counts are runtime- and JIT-dependent).
/// </remarks>
public sealed class RateLimitPolicyAllocationTests
{
    private const int WarmupInvocations = 1_000;
    private const int MeasuredInvocations = 1_000;

    [Test]
    public async Task Constant_Key_Single_Tier_Allow_Path_Stays_Within_Budget()
    {
        // A budget with far more than 1_000 tokens keeps every measured invocation on
        // the allow path (MaxTokens caps any refill at this scale, so the bucket cannot
        // overshoot into a rejection).
        using var policy = RateLimitPolicy
            .Create("perf")
            .Tier(
                "instance",
                RateLimitBudget.PerMinute(1_000_000, 1),
                RateLimitKeys.Constant("instance")
            )
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        // A plain WebContext and a Constant key: Path()/RemoteAddress() classification
        // would add their own allocations to the measurement.
        var context = WebContext.Create(
            new HttpRequest { Method = "GET", Target = "/api/limited", Path = "/api/limited" }
        );

        // The stub returns an already-completed ValueTask, so the awaits never hop
        // threads — GC.GetAllocatedBytesForCurrentThread() is per-thread and would
        // otherwise measure garbage. The response is built once and reused: a fresh
        // HttpResponse per call would put the harness's own header-collection growth
        // (hundreds of bytes) into the number, not the middleware's. The preset
        // X-RateLimit-Limit makes the guard true from the first call: the group-level
        // guard suppresses the header writes (reuse alone would trip it after the
        // first iteration), so the measured delta is the evaluator's own allow-path
        // allocation — the RateLimitState it injects for downstream handlers (~48 B).
        var response = new HttpResponse { StatusCode = 200 };
        response.Headers.Add("X-RateLimit-Limit", "sentinel");
        var allowCalls = 0;
        WebRequestHandler next = (_, _) =>
        {
            allowCalls++;
            return ValueTask.FromResult(response);
        };

        // Warm-up outside the measured window (JIT, static init, the first dictionary
        // insert into WebContext.Items).
        for (var i = 0; i < WarmupInvocations; i++)
            await middleware(context, next, CancellationToken.None);

        // The counter must be read as a delta: the warm-up above already spent
        // invocations through the same stub.
        var allowCallsBefore = allowCalls;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredInvocations; i++)
            await middleware(context, next, CancellationToken.None);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var bytesPerRequest = bytes / (double)MeasuredInvocations;
        Console.WriteLine(
            $"rate-limit-policy-allow-allocation invocations={MeasuredInvocations} "
                + $"bytes={bytes} bytesPerRequest={bytesPerRequest:F1}"
        );

        // The loop must have stayed on the allow path for the number to mean anything:
        // the reject path returns without calling next, so the counter is the real
        // proof (the Items key alone is vacuous — the warm-up already wrote it).
        await Assert.That(allowCalls - allowCallsBefore).IsEqualTo(MeasuredInvocations);
        await Assert.That(context.Items.ContainsKey(WebContextKeys.RateLimitState)).IsTrue();

        // Regression tripwire, NOT a promise about the number: the allow path is
        // Regression tripwire, NOT a promise about the number: the allow path is
        // expected to allocate one RateLimitState and nothing else. ~1 KiB only
        // catches pathological (>= 1 KiB/request) regressions — a per-request
        // closure is ~96 B and a rebuilt response ~480 B, both under it by design.
        await Assert.That(bytesPerRequest).IsLessThan(1024d);
    }
}
