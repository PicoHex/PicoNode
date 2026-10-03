namespace PicoNode.Web;

/// <summary>
/// Builds a frozen <see cref="RateLimitPolicy"/>. Chainable; the policy is created by
/// <see cref="Build"/>, which validates, creates one store per tier and freezes the tier list.
/// </summary>
/// <remarks>
/// There is no implicit public constructor: <see cref="RateLimitPolicy.Create"/> is the only
/// entry point, so the builder does not appear as a concrete-constructible type in the public
/// API baseline. Configuration is a build-time-only activity; the delegates registered here
/// are compiled once, never per request.
/// </remarks>
public sealed class RateLimitPolicyBuilder
{
    private readonly string _name;
    private readonly List<RateLimitTier> _tiers = [];
    private readonly List<string> _exemptPrefixes = [];
    private Predicate<WebContext>? _applies;
    private Action<RateLimitRejection>? _onRejected;
    private bool _failOpen;

    internal RateLimitPolicyBuilder(string name)
    {
        // Fail here, not at UseRateLimit: the registry's dictionary would otherwise
        // throw its own ArgumentNullException with no parameter name of ours, after
        // the caller has already started composing the pipeline.
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
    }

    /// <summary>
    /// Clock handed to every store <see cref="Build"/> creates. Internal test seam
    /// (deterministic refill tests inject a manual clock); not part of the public API.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Adds a tier. Order is preserved: the evaluator walks tiers in declaration order and the
    /// first tier that reports a rejection wins.
    /// </summary>
    /// <param name="name">Tier name; must be unique (Ordinal) within the policy.</param>
    /// <param name="budget">Token-bucket parameters; rejected by <see cref="Build"/> unless <c>MaxTokens &gt; 0</c> and <c>RefillInterval &gt; 0</c>.</param>
    /// <param name="key">Key selector; returning <c>null</c> means "tier not applicable to this request".</param>
    /// <param name="applies">Optional tier-level predicate; <c>false</c> skips the tier.</param>
    public RateLimitPolicyBuilder Tier(
        string name,
        RateLimitBudget budget,
        Func<WebContext, string?> key,
        Predicate<WebContext>? applies = null
    )
    {
        _tiers.Add(new RateLimitTier(name, budget, key, applies));
        return this;
    }

    /// <summary>
    /// Adds a path prefix whose requests are exempt from the whole policy. Matching is literal
    /// and Ordinal, on a segment boundary (<c>/api</c> matches <c>/api</c> and <c>/api/x</c>, but
    /// not <c>/apix</c>); repeated calls accumulate as an OR whitelist.
    /// </summary>
    public RateLimitPolicyBuilder Exempt(string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(pathPrefix);
        _exemptPrefixes.Add(pathPrefix);
        return this;
    }

    /// <summary>
    /// Sets the policy-level applicability predicate. It composes with the
    /// <see cref="Exempt"/> whitelist using AND — an exempt path stays exempt even when the
    /// predicate is <c>true</c> (spec §3.4).
    /// </summary>
    public RateLimitPolicyBuilder Applies(Predicate<WebContext> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _applies = predicate;
        return this;
    }

    /// <summary>
    /// Sets the rejection callback, invoked synchronously at most once per rejected request.
    /// The callback must not retain the <see cref="RateLimitRejection"/> (it holds a
    /// <see cref="WebContext"/>); copy out the scalars it needs.
    /// </summary>
    public RateLimitPolicyBuilder OnRejected(Action<RateLimitRejection> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onRejected = handler;
        return this;
    }

    /// <summary>
    /// Opts into fail-open: store failures allow the request through. The default
    /// (<see cref="RateLimitPolicy.FailOpen"/> <c>false</c>) is the shield semantics — a broken
    /// store rejects rather than opens the gate.
    /// </summary>
    /// <remarks>
    /// "Store failure" includes the post-<see cref="System.IDisposable.Dispose"/>
    /// <see cref="ObjectDisposedException"/> (spec §3.1.1): this tier is then skipped and the
    /// request counts as allowed, and <see cref="OnRejected"/> is <em>not</em> invoked (it
    /// reports rejections, and fail-open produces none). Disposing a policy that is still
    /// serving traffic therefore silently disables its tiers.
    /// </remarks>
    public RateLimitPolicyBuilder FailOpen()
    {
        _failOpen = true;
        return this;
    }

    /// <summary>
    /// Validates the configuration, creates the per-tier stores and freezes the policy.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tier was added, or two tiers share a name (Ordinal).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A tier's <c>MaxTokens</c> or <c>RefillInterval</c> is not positive. <c>RefillRate = 0</c> (fixed window) and negative rates are legal.</exception>
    public RateLimitPolicy Build()
    {
        // 1. Validate.
        var tiers = _tiers.ToArray(); // frozen copy, not LINQ
        if (tiers.Length == 0)
        {
            throw new InvalidOperationException(
                "A rate-limit policy must declare at least one tier."
            );
        }

        for (var i = 0; i < tiers.Length; i++)
        {
            for (var j = i + 1; j < tiers.Length; j++)
            {
                if (string.Equals(tiers[i].Name, tiers[j].Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Duplicate rate-limit tier name '{tiers[i].Name}'. Tier names feed "
                            + "observation and RateLimitRejection.Tier, so they must be unique."
                    );
                }
            }
        }

        for (var i = 0; i < tiers.Length; i++)
        {
            var budget = tiers[i].Budget;
            // RefillRate is deliberately NOT range-checked: 0 is a fixed window and a
            // negative rate behaves like 0 (see InMemoryRateLimitStore).
            if (budget.MaxTokens <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    $"tiers[{i}].Budget.MaxTokens",
                    budget.MaxTokens,
                    $"Tier '{tiers[i].Name}' must have MaxTokens > 0."
                );
            }

            if (budget.RefillInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    $"tiers[{i}].Budget.RefillInterval",
                    budget.RefillInterval,
                    $"Tier '{tiers[i].Name}' must have RefillInterval > 0."
                );
            }
        }

        // 2. Create one store per tier (the store caps tokens and never refills at rate 0).
        var clock = TimeProvider;
        var stores = new InMemoryRateLimitStore[tiers.Length];
        for (var i = 0; i < tiers.Length; i++)
        {
            var budget = tiers[i].Budget;
            stores[i] = new InMemoryRateLimitStore(in budget) { TimeProvider = clock };
        }

        // 3. Construct the policy; the ctor is the single store entry point.
        return new RateLimitPolicy(
            _name,
            tiers,
            stores,
            _failOpen,
            _applies,
            CompileExempt(_exemptPrefixes),
            _onRejected
        );
    }

    /// <summary>
    /// Compiles the accumulated <see cref="Exempt"/> prefixes into one predicate — their OR —
    /// or <c>null</c> when none was registered. Runs at build time; the closure captures the
    /// frozen prefix array only.
    /// </summary>
    private static Predicate<WebContext>? CompileExempt(List<string> prefixes)
    {
        if (prefixes.Count == 0)
        {
            return null;
        }

        var frozen = prefixes.ToArray();
        if (frozen.Length == 1)
        {
            var only = frozen[0];
            return ctx => RateLimitSegmentPrefix.Matches(ctx.PathMemory.Span, only);
        }

        return ctx =>
        {
            var path = ctx.PathMemory.Span;
            for (var i = 0; i < frozen.Length; i++)
            {
                if (RateLimitSegmentPrefix.Matches(path, frozen[i]))
                {
                    return true;
                }
            }

            return false;
        };
    }
}
