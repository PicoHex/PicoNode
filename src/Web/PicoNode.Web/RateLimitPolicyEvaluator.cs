namespace PicoNode.Web;

/// <summary>
/// Evaluates a <see cref="RateLimitPolicy"/> — the multi-tier counterpart of the legacy
/// single-bucket <see cref="RateLimitMiddleware"/> (spec §3.2).
/// </summary>
/// <remarks>
/// <see cref="Create"/>'s only allocation is the returned <see cref="WebMiddleware"/>:
/// every delegate, array and scalar it needs is read out of the policy once, and the tier
/// list is walked with a <c>for</c> loop (no LINQ, no enumerator, no closure over
/// per-request state). The evaluator's own allow path then allocates one
/// <see cref="RateLimitState"/> per request (measured 48 B) and nothing else; the
/// <c>X-RateLimit-*</c> values <see cref="RateLimitResponses.AddHeaders"/> writes after
/// <c>next</c> returns are three short numeric strings of their own, and they are
/// skipped entirely when the caller already set one of the group's headers.
/// <para>
/// Ordering contract (spec §3.2.4/§3.2.5): the <see cref="WebContextKeys.RateLimitState"/>
/// entry is written <em>before</em> <c>await next</c> so downstream handlers can read it,
/// while <see cref="RateLimitResponses.AddHeaders"/> runs <em>after</em> <c>next</c>
/// returns, on the response. Rejections return before <c>next</c> is ever invoked.
/// </para>
/// </remarks>
internal static class RateLimitPolicyEvaluator
{
    /// <summary>
    /// Builds the middleware for <paramref name="policy"/>. The policy stays owned by the
    /// host (spec §3.1.1) — the middleware never disposes it.
    /// </summary>
    internal static WebMiddleware Create(RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Read once, never per request. The policy is frozen by Build() and its arrays are
        // parallel (stores[i] belongs to tiers[i]), so indexing both by i costs nothing.
        var tiers = policy.Tiers;
        var stores = policy.Stores;
        var policyName = policy.Name;
        var failOpen = policy.FailOpen;
        var onRejected = policy.OnRejected;

        return async (ctx, next, ct) =>
        {
            // The policy gate (policy predicate AND the exempt whitelist) runs outside the
            // store try: a throwing classifier is a programming error and must surface.
            if (!policy.ShouldApply(ctx))
                return await next(ctx, ct);

            RateLimitResult? winner = null;
            RateLimitTier? winnerTier = null;

            for (var i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];

                // Classifiers run outside the try (a throwing predicate is a bug, not a
                // store failure — it must not fail open).
                if (tier.Applies is { } applies && !applies(ctx))
                    continue;

                var raw = tier.Key(ctx);
                if (raw is null)
                    continue; // tier not applicable: no bucket, no state, no headers

                var key = SanitizeKey(raw);

                RateLimitResult result;
                try
                {
                    result = await stores[i].TryConsumeTokenAsync(key, ct);
                }
                catch when (failOpen)
                {
                    // Fail-open skips this tier only; tokens spent by earlier tiers are
                    // deliberately not refunded (token buckets have no refund).
                    continue;
                }
                catch
                {
                    // Fail-closed (the default): an unreachable store is a rejection.
                    // There is no token-bucket result for a failing store, hence the
                    // default result in the notification.
                    NotifyRejected(
                        onRejected,
                        policyName,
                        tier,
                        RateLimitRejectionReason.StoreError,
                        default,
                        ctx
                    );
                    return RateLimitResponses.RejectedOnStoreError(tier.Budget.MaxTokens);
                }

                if (!result.Allowed)
                {
                    // The rejecting tier's real result travels with the rejection and
                    // drives the response.
                    NotifyRejected(
                        onRejected,
                        policyName,
                        tier,
                        RateLimitRejectionReason.LimitReached,
                        result,
                        ctx
                    );
                    return RateLimitResponses.Rejected(result);
                }

                if (winner is null)
                {
                    // First declarer wins the state and the headers; later matching
                    // tiers still consume their token.
                    winner = result;
                    winnerTier = tier;
                }
            }

            if (winner is not null)
            {
                ctx.Items[WebContextKeys.RateLimitState] = new RateLimitState
                {
                    Limit = winner.Value.Limit,
                    Remaining = winner.Value.Remaining,
                    NextAvailableAt = winner.Value.NextAvailableAt,
                    Policy = policyName,
                    Tier = winnerTier!.Name,
                };
            }

            var response = await next(ctx, ct);

            if (winner is not null)
                RateLimitResponses.AddHeaders(response, winner.Value);

            return response;
        };
    }

    /// <summary>
    /// Reports a rejection to the policy's <see cref="RateLimitPolicy.OnRejected"/> — exactly
    /// once, on the two 429 paths only (spec §3.2.7). A <c>null</c> handler returns before the
    /// <see cref="RateLimitRejection"/> is constructed, so the unset case costs a null check.
    /// </summary>
    /// <param name="onRejected">The handler, read out of the policy once in <see cref="Create"/>.</param>
    /// <param name="policyName">The frozen <see cref="RateLimitPolicy.Name"/>, for the same reason.</param>
    /// <param name="tier">The rejecting (winning) tier.</param>
    /// <param name="reason">Which 429 path this is.</param>
    /// <param name="result">
    /// The tier's real result on <see cref="RateLimitRejectionReason.LimitReached"/>;
    /// <c>default</c> on <see cref="RateLimitRejectionReason.StoreError"/>.
    /// </param>
    /// <param name="ctx">The rejected request's context.</param>
    /// <remarks>
    /// The handler runs synchronously on the 429 path. Spec §3.2.7 does not define a
    /// throwing handler: an exception escapes the middleware (and becomes a 500 under an
    /// exception handler) instead of the 429 being returned — it is deliberately not
    /// swallowed here.
    /// </remarks>
    private static void NotifyRejected(
        Action<RateLimitRejection>? onRejected,
        string policyName,
        RateLimitTier tier,
        RateLimitRejectionReason reason,
        RateLimitResult result,
        WebContext ctx
    )
    {
        if (onRejected is null)
            return;

        onRejected(new RateLimitRejection(policyName, tier.Name, reason, result, ctx));
    }

    /// <summary>
    /// Normalizes a tier key for the store: truncates to 256 chars and maps CR/LF to
    /// <c>'_'</c> (parity with the legacy selector path). Non-async on purpose — the slow
    /// path uses <c>stackalloc</c>.
    /// </summary>
    /// <remarks>
    /// The fast path returns <paramref name="key"/> itself (zero allocation); only a key
    /// that is too long or contains a line break is rebuilt. Unlike the legacy path there
    /// is no <c>"anonymous"</c> fallback — a tier whose selector returns <c>null</c> is
    /// simply not applicable (spec §3.1).
    /// </remarks>
    private static string SanitizeKey(string key)
    {
        if (key.Length <= 256 && !key.AsSpan().ContainsAny('\r', '\n'))
            return key;

        var length = key.Length > 256 ? 256 : key.Length;
        Span<char> buffer = stackalloc char[length];
        key.AsSpan(0, length).CopyTo(buffer);

        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (c == '\r' || c == '\n')
                buffer[i] = '_';
        }

        return new string(buffer);
    }
}
