namespace PicoNode.AI.Tests.LLm;

/// <summary>
/// Batch 2 (A2): ResilientLLmClient must forward events as they arrive from the underlying
/// client, not buffer the entire stream first. Retry logic remains active only until the
/// first event has been observed.
/// </summary>
public class ResilientLLmClientStreamingTests
{
    [Test]
    public async Task StreamAsync_ForwardsEventsWithoutWaitingForStreamCompletion()
    {
        // The producer yields one event and then blocks forever on a cancellation token.
        // If the resilient client buffers the whole stream before yielding, the consumer
        // will never see the first event within the timeout window.
        var neverComplete = new TaskCompletionSource();

        async IAsyncEnumerable<AssistantMessageEvent> Producer(
            [EnumeratorCancellation] CancellationToken producerCt
        )
        {
            yield return new AssistantMessageEvent.TextDelta
            {
                Index = 0,
                Delta = "first",
                Partial = new Message { Role = "assistant" },
            };
            await neverComplete.Task.WaitAsync(producerCt);
#pragma warning disable CS0162
            yield return new AssistantMessageEvent.TextDelta
            {
                Index = 1,
                Delta = "never",
                Partial = new Message { Role = "assistant" },
            };
#pragma warning restore CS0162
        }

        var provider = new ProviderConfig
        {
            Name = "p",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(
            provider,
            new DelegateLLmClient((_, _, _, ct) => Producer(ct))
        );

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string? first = null;
        await foreach (
            var evt in resilient.StreamAsync(
                new Model { Id = "m", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                null,
                timeout.Token
            )
        )
        {
            if (evt is AssistantMessageEvent.TextDelta td)
            {
                first = td.Delta;
                break;
            }
        }

        await Assert.That(first).IsEqualTo("first");
    }

    [Test]
    public async Task StreamAsync_RetriesWhenFirstAttemptFailsBeforeAnyEvent()
    {
        int calls = 0;

        async IAsyncEnumerable<AssistantMessageEvent> Producer()
        {
            calls++;
            if (calls == 1)
                throw new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
            await Task.Yield();
            yield return new AssistantMessageEvent.TextDelta
            {
                Index = 0,
                Delta = "ok",
                Partial = new Message { Role = "assistant" },
            };
        }

        var provider = new ProviderConfig
        {
            Name = "p",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, new DelegateLLmClient((_, _, _, _) => Producer()));

        var got = new List<string>();
        await foreach (
            var evt in resilient.StreamAsync(
                new Model { Id = "m", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                null,
                CancellationToken.None
            )
        )
        {
            if (evt is AssistantMessageEvent.TextDelta td)
                got.Add(td.Delta);
        }

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0]).IsEqualTo("ok");
    }

    [Test]
    public async Task StreamAsync_DoesNotRetryAfterFirstEventYielded()
    {
        int calls = 0;

        async IAsyncEnumerable<AssistantMessageEvent> Producer()
        {
            calls++;
            yield return new AssistantMessageEvent.TextDelta
            {
                Index = 0,
                Delta = "partial",
                Partial = new Message { Role = "assistant" },
            };
            await Task.Yield();
            throw new HttpRequestException(
                "mid-stream failure",
                null,
                HttpStatusCode.ServiceUnavailable
            );
        }

        var breaker = new CountingCircuitBreaker();
        var provider = new ProviderConfig
        {
            Name = "p",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(
            provider,
            new DelegateLLmClient((_, _, _, _) => Producer()),
            breaker
        );

        var got = new List<string>();
        await Assert
            .That(async () =>
            {
                await foreach (
                    var evt in resilient.StreamAsync(
                        new Model { Id = "m", Api = AiApiFormat.OpenAIChatCompletions },
                        EmptyContext(),
                        null,
                        CancellationToken.None
                    )
                )
                {
                    if (evt is AssistantMessageEvent.TextDelta td)
                        got.Add(td.Delta);
                }
            })
            .Throws<HttpRequestException>();

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0]).IsEqualTo("partial");
        await Assert.That(breaker.FailureCount).IsGreaterThanOrEqualTo(1);
    }

    // ── Helpers (mirror ResilientLLmClientResilienceTests) ──

    private static ResilientLLmClient BuildResilient(
        ProviderConfig provider,
        ILLmClient client,
        ICircuitBreaker? breaker = null
    )
    {
        var breakers = new Dictionary<string, ICircuitBreaker>();
        if (breaker is not null)
            breakers[provider.Name] = breaker;
        return new ResilientLLmClient(
            new ProviderRouter([provider]),
            new Dictionary<string, ProviderConfig> { [provider.Name] = provider },
            breakers,
            new Dictionary<string, ILLmClient> { [provider.Name] = client }
        );
    }

    private static ChatContext EmptyContext() =>
        new()
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = "hi",
                    Timestamp = 1,
                },
            ],
        };
}
