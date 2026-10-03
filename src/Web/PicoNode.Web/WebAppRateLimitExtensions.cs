namespace PicoNode.Web;

/// <summary>
/// Mounts <see cref="RateLimitPolicy"/> instances on a <see cref="WebApp"/> (spec §3.4).
/// </summary>
/// <remarks>
/// Register a policy <b>before</b> <c>AuthMiddleware</c>: the pre-auth shield (cheap
/// classification ahead of PBKDF2) is the premise of the policy design. Each policy becomes
/// one middleware layer, and registration order is outer to inner.
/// <para>
/// <see cref="UseRateLimit"/> does <b>not</b> take ownership of the policy: the host owns
/// disposal (<c>using var policy = …</c>; spec §3.1.1), exactly as a host-owned
/// <c>InMemoryRateLimitStore</c> is disposed today. <see cref="WebApp"/> itself has no
/// dispose semantics, so it cannot dispose the policies it mounts.
/// </para>
/// </remarks>
public static class WebAppRateLimitExtensions
{
    /// <summary>
    /// Registers <paramref name="policy"/> with the app and appends its middleware to the
    /// pipeline, returning <paramref name="app"/> for chaining. A later call wraps an earlier
    /// one: registration order is outer to inner.
    /// </summary>
    /// <param name="app">The app to mount on.</param>
    /// <param name="policy">The policy to mount; the caller keeps ownership and disposes it.</param>
    /// <returns><paramref name="app"/>, so calls chain like <see cref="WebApp.Use"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> or <paramref name="policy"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A policy whose <see cref="RateLimitPolicy.Name"/> (Ordinal) is already registered on this app.</exception>
    public static WebApp UseRateLimit(this WebApp app, RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(policy);

        app.RateLimits.Register(policy);
        app.Use(RateLimitMiddleware.Create(policy));
        return app;
    }
}

/// <summary>
/// The startup-time registry of the policies mounted on one <see cref="WebApp"/>, keyed by
/// <see cref="RateLimitPolicy.Name"/> (Ordinal). Internal: it is not part of the public API.
/// </summary>
/// <remarks>
/// Registration happens at startup only (there is no concurrency here): it exists so a
/// duplicate policy name is rejected with <see cref="InvalidOperationException"/> — policy
/// names feed observation and future route mounting, so they must be unique per host.
/// </remarks>
internal sealed class RateLimitPolicyRegistry
{
    private readonly Dictionary<string, RateLimitPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>The number of registered policies.</summary>
    internal int Count => _policies.Count;

    /// <summary>Whether a policy with this exact name (Ordinal) is registered.</summary>
    internal bool Contains(string name) => _policies.ContainsKey(name);

    /// <summary>Registers <paramref name="policy"/>, rejecting an already-used name.</summary>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    internal void Register(RateLimitPolicy policy)
    {
        if (!_policies.TryAdd(policy.Name, policy))
        {
            throw new InvalidOperationException(
                $"A rate-limit policy named '{policy.Name}' is already registered. Policy names "
                    + "feed observation and mounting, so they must be unique per host."
            );
        }
    }
}
