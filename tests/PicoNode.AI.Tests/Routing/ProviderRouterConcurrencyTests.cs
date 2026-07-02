
namespace PicoNode.Agent.Tests.AI;

/// <summary>
/// Design-review flaw #11 (P2): ProviderRouter.UpdateProviders replaces the
/// internal _providers reference without synchronization, and Resolve reads it
/// multiple times. Concurrent UpdateProviders + Resolve can therefore observe
/// two different snapshots (rare tear) or throw InvalidOperationException
/// if enumeration crosses the swap.
///
/// Fix contract: Resolve must observe a consistent snapshot of the provider
/// list for the duration of the call, and concurrent Update+Resolve loops must
/// complete without throwing.
/// </summary>
public class ProviderRouterConcurrencyTests
{
    [Test]
    public async Task ConcurrentUpdateAndResolve_DoesNotThrow()
    {
        var router = new ProviderRouter(BuildProviders("initial"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = cts.Token;

        var updater = Task.Run(
            () =>
            {
                var i = 0;
                while (!token.IsCancellationRequested)
                {
                    router.UpdateProviders(BuildProviders($"gen{i}"));
                    i++;
                }
            },
            token
        );

        var resolvers = Enumerable
            .Range(0, 4)
            .Select(idx =>
                Task.Run(
                    () =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            _ = router.Resolve("model-a", AiApiFormat.OpenAIChatCompletions);
                            _ = router.Resolve(null, null);
                        }
                    },
                    token
                )
            )
            .ToArray();

        try
        {
            await Task.WhenAll(new[] { updater }.Concat(resolvers));
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        // Pass = no unhandled exception propagated from the resolver loops.
    }

    [Test]
    public async Task Resolve_UsesConsistentSnapshot_WhenPreferredFormatFilterEmpties()
    {
        // If the format filter drops all candidates, Resolve falls back to
        // _providers. Under torn reads, the fallback list could differ from
        // the filtered candidates' source, producing surprising results.
        // Post-fix: same underlying snapshot must be used for both branches.
        var router = new ProviderRouter(
            new[]
            {
                new ProviderConfig
                {
                    Name = "only-anthropic",
                    BaseUrl = "http://x",
                    ApiFormat = AiApiFormat.AnthropicMessages,
                    Priority = 1,
                },
            }
        );

        // preferredFormat filters everything out → fallback should still return the anthropic entry.
        var resolved = router.Resolve("m", AiApiFormat.OpenAIChatCompletions);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).IsEqualTo("only-anthropic");
    }

    private static IEnumerable<ProviderConfig> BuildProviders(string tag) =>
        new[]
        {
            new ProviderConfig
            {
                Name = $"{tag}-a",
                BaseUrl = "http://a",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 1,
            },
            new ProviderConfig
            {
                Name = $"{tag}-b",
                BaseUrl = "http://b",
                ApiFormat = AiApiFormat.AnthropicMessages,
                Priority = 2,
            },
        };
}
