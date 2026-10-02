namespace PicoNode.Web;

/// <summary>Why a request was rejected by a <see cref="RateLimitPolicy"/>.</summary>
public enum RateLimitRejectionReason
{
    /// <summary>The winning tier's bucket was exhausted.</summary>
    LimitReached,

    /// <summary>The winning tier's store threw; the policy's fail-open setting decides the response.</summary>
    StoreError,
}

/// <summary>
/// The rejection handed to <see cref="RateLimitPolicy.OnRejected"/>: which policy, which
/// tier, and why.
/// </summary>
/// <remarks>
/// <b>Synchronous consumption only.</b> A rejection holds a <see cref="WebContext"/>
/// reference, so the callback must not retain the rejection (no storage, no capture in a
/// closure, no queueing) — doing so pins the request context and its buffers. Copy the
/// scalar fields (<see cref="Policy"/>, <see cref="Tier"/>, <see cref="Reason"/>,
/// <see cref="Result"/>) out instead, and do not materialize
/// <c>Context.Path</c> while counting, since that allocates on the rejection path.
/// </remarks>
public readonly struct RateLimitRejection
{
    internal RateLimitRejection(
        string policy,
        string tier,
        RateLimitRejectionReason reason,
        RateLimitResult result,
        WebContext context
    )
    {
        Policy = policy;
        Tier = tier;
        Reason = reason;
        Result = result;
        Context = context;
    }

    /// <summary>The rejecting policy's name.</summary>
    public string Policy { get; }

    /// <summary>The winning (rejecting) tier's name.</summary>
    public string Tier { get; }

    /// <summary>Why the request was rejected.</summary>
    public RateLimitRejectionReason Reason { get; }

    /// <summary>
    /// The winning tier's result. The zero value when <see cref="Reason"/> is
    /// <see cref="RateLimitRejectionReason.StoreError"/> (there is no token-bucket result
    /// for a failing store).
    /// </summary>
    public RateLimitResult Result { get; }

    /// <summary>
    /// The rejected request's context. Valid only for the duration of the
    /// <see cref="RateLimitPolicy.OnRejected"/> call — see the type remarks.
    /// </summary>
    public WebContext Context { get; }
}
