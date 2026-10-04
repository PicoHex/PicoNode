namespace PicoNode.Docs.Tests;

/// <summary>
/// The rate-limiting code samples from the READMEs, verbatim, so they cannot rot: this file
/// compiles them, and <see cref="ReadmeSamplesTests"/> fails when a README drifts from the
/// marked regions. All ten READMEs share one code block per sample (only the prose is
/// translated), so both checks run against every language.
/// </summary>
internal static class ReadmeSamples
{
    /// <summary>Stand-in for the application's own rejection counter.</summary>
    private sealed class Metrics
    {
        public void CountRejection(string policy, string tier, RateLimitRejectionReason reason) { }
    }

    /// <summary>The "Rate Limiting" policy sample.</summary>
    public static void Policy(WebApp app, string ownerToken)
    {
        var metrics = new Metrics();
        // #region readme:policy
        using var policy = RateLimitPolicy.Create("web")
            .Exempt("/api/health")                                  // OR-accumulating path whitelist (segment-boundary prefix)
            .Applies(RateLimitPath.Prefix("/api"))                  // policy scope: must pass this AND not be exempt
            .Tier("trusted",   RateLimitBudget.PerSecond(300, 30), RateLimitKeys.TokenMatch(ownerToken))
            .Tier("anonymous", RateLimitBudget.PerSecond(60, 1),   RateLimitKeys.Not(RateLimitKeys.TokenMatch(ownerToken), "instance"))
            .OnRejected(r => metrics.CountRejection(r.Policy, r.Tier, r.Reason))   // LimitReached | StoreError
            .Build();

        app.UseRateLimit(policy);   // register before AuthMiddleware (the pre-auth shield); the host owns disposal
        // #endregion
    }

    /// <summary>The single-bucket (legacy middleware) sample.</summary>
    public static void Bucket(WebApp app)
    {
        // #region readme:bucket
        // Single bucket: the original middleware (one store, one key selector)
        var bucket = RateLimitMiddleware.Create(new RateLimitOptions
        {
            MaxTokens = 60,
            RefillRate = 1,
            RefillInterval = TimeSpan.FromSeconds(1),   // ~1 token/s, burst 60
            KeySelector = RateLimitKeys.RemoteAddress(),   // a null key shares the "anonymous" bucket
        });
        app.Use(bucket);
        // #endregion
    }
}
