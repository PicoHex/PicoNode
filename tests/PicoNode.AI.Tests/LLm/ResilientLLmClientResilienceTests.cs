namespace PicoNode.AI.Tests.LLm;


public class ResilientLLmClientResilienceTests
{
    // ── A6: ApiKey flows from ProviderConfig into StreamOptions ──

    [Test]
    public async Task StreamAsync_InjectsProviderApiKey_WhenUserOptionsHasNoKey()
    {
        StreamOptions? captured = null;
        var client = new DelegateLLmClient(
            (_, _, opts, _) =>
            {
                captured = opts;
                return AsyncEnum(EmptyDelta());
            }
        );
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiKey = "sk-from-config",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client);

        await Consume(
            resilient.StreamAsync(
                new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                null,
                CancellationToken.None
            )
        );

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.ApiKey).IsEqualTo("sk-from-config");
    }

    [Test]
    public async Task StreamAsync_UserOptionsApiKey_TakesPrecedenceOverProviderApiKey()
    {
        StreamOptions? captured = null;
        var client = new DelegateLLmClient(
            (_, _, opts, _) =>
            {
                captured = opts;
                return AsyncEnum(EmptyDelta());
            }
        );
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiKey = "sk-from-config",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client);

        await Consume(
            resilient.StreamAsync(
                new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                new StreamOptions { ApiKey = "sk-explicit" },
                CancellationToken.None
            )
        );

        await Assert.That(captured!.ApiKey).IsEqualTo("sk-explicit");
    }

    // ── A3: RecordFailure/RecordSuccess must be invoked on the CircuitBreaker ──

    [Test]
    public async Task StreamAsync_OnSuccess_RecordsSuccess()
    {
        var breaker = new CountingCircuitBreaker();
        var client = new DelegateLLmClient((_, _, _, _) => AsyncEnum(EmptyDelta()));
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client, breaker);

        await Consume(
            resilient.StreamAsync(
                new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                null,
                CancellationToken.None
            )
        );

        await Assert.That(breaker.SuccessCount).IsEqualTo(1);
        await Assert.That(breaker.FailureCount).IsEqualTo(0);
    }

    [Test]
    public async Task StreamAsync_WhenClientThrowsRetryableExceptionOnEveryAttempt_RecordsFailurePerAttempt()
    {
        var breaker = new CountingCircuitBreaker();
        var client = new DelegateLLmClient(
            (_, _, _, _) =>
                Throwing<AssistantMessageEvent>(
                    new HttpRequestException("upstream", null, HttpStatusCode.ServiceUnavailable)
                )
        );
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client, breaker);

        await Assert
            .That(async () =>
                await Consume(
                    resilient.StreamAsync(
                        new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                        EmptyContext(),
                        null,
                        CancellationToken.None
                    )
                )
            )
            .Throws<HttpRequestException>();

        // 1 initial + 3 retries = 4 attempts, each records a failure
        await Assert.That(breaker.FailureCount).IsGreaterThanOrEqualTo(4);
        await Assert.That(breaker.SuccessCount).IsEqualTo(0);
    }

    [Test]
    public async Task StreamAsync_WhenClientYieldsErrorEvent_RecordsFailure()
    {
        var breaker = new CountingCircuitBreaker();
        var client = new DelegateLLmClient(
            (_, _, _, _) =>
                AsyncEnum(
                    new AssistantMessageEvent.Error
                    {
                        Message = new Message
                        {
                            Role = "assistant",
                            ErrorMessage = "boom",
                            StopReason = "error",
                        },
                    }
                )
        );
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client, breaker);

        await Consume(
            resilient.StreamAsync(
                new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                null,
                CancellationToken.None
            )
        );

        await Assert.That(breaker.FailureCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(breaker.SuccessCount).IsEqualTo(0);
    }

    // ── A5: IsRetryable must use exception type + HttpStatusCode, not Message.Contains ──

    [Test]
    public async Task StreamAsync_NonRetryableHttpStatus_DoesNotRetry()
    {
        int attempts = 0;
        var client = new DelegateLLmClient(
            (_, _, _, _) =>
            {
                attempts++;
                return Throwing<AssistantMessageEvent>(
                    new HttpRequestException("bad input", null, HttpStatusCode.BadRequest)
                );
            }
        );
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client);

        await Assert
            .That(async () =>
                await Consume(
                    resilient.StreamAsync(
                        new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                        EmptyContext(),
                        null,
                        CancellationToken.None
                    )
                )
            )
            .Throws<HttpRequestException>();

        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task StreamAsync_UnrelatedExceptionWithRetryLikeMessage_DoesNotRetry()
    {
        // "rate" appears in the message but exception type is not network-related.
        // Old string-matching implementation would incorrectly retry this.
        int attempts = 0;
        var client = new DelegateLLmClient(
            (_, _, _, _) =>
            {
                attempts++;
                return Throwing<AssistantMessageEvent>(
                    new InvalidOperationException("failed to generate rate limit token")
                );
            }
        );
        var provider = new ProviderConfig
        {
            Name = "openai",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
        };
        var resilient = BuildResilient(provider, client);

        await Assert
            .That(async () =>
                await Consume(
                    resilient.StreamAsync(
                        new Model { Id = "gpt", Api = AiApiFormat.OpenAIChatCompletions },
                        EmptyContext(),
                        null,
                        CancellationToken.None
                    )
                )
            )
            .Throws<InvalidOperationException>();

        await Assert.That(attempts).IsEqualTo(1);
    }

    // ── A4: TryFailover must preserve full ProviderConfig (BaseUrl / ApiKey / ApiFormat) ──

    [Test]
    public async Task Failover_WhenPrimaryCircuitOpen_UsesFallbackWithFullConfig()
    {
        Model? capturedModel = null;
        StreamOptions? capturedOptions = null;
        var fallbackClient = new DelegateLLmClient(
            (m, _, opts, _) =>
            {
                capturedModel = m;
                capturedOptions = opts;
                return AsyncEnum(EmptyDelta());
            }
        );
        var primary = new ProviderConfig
        {
            Name = "openai",
            ApiKey = "sk-openai",
            BaseUrl = "https://api.openai.com/v1",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
            Priority = 1,
        };
        var fallback = new ProviderConfig
        {
            Name = "anthropic",
            ApiKey = "sk-anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiFormat = AiApiFormat.AnthropicMessages,
            Priority = 2,
        };
        var resilient = new ResilientLLmClient(
            new ProviderRouter([primary, fallback]),
            new Dictionary<string, ProviderConfig>
            {
                ["openai"] = primary,
                ["anthropic"] = fallback,
            },
            new Dictionary<string, ICircuitBreaker>
            {
                ["openai"] = new AlwaysOpenCircuitBreaker(),
                ["anthropic"] = new CountingCircuitBreaker(),
            },
            new Dictionary<string, ILLmClient>
            {
                ["openai"] = new UnreachableLLmClient(),
                ["anthropic"] = fallbackClient,
            }
        );

        await Consume(
            resilient.StreamAsync(
                new Model { Id = "any", Api = AiApiFormat.OpenAIChatCompletions },
                EmptyContext(),
                null,
                CancellationToken.None
            )
        );

        await Assert.That(capturedModel).IsNotNull();
        await Assert.That(capturedModel!.BaseUrl).IsEqualTo("https://api.anthropic.com");
        await Assert.That(capturedModel.Api).IsEqualTo(AiApiFormat.AnthropicMessages);
        await Assert.That(capturedModel.Provider).IsEqualTo("anthropic");
        await Assert.That(capturedOptions!.ApiKey).IsEqualTo("sk-anthropic");
    }

    // ── Helpers ──

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

    private static AssistantMessageEvent EmptyDelta() =>
        new AssistantMessageEvent.TextDelta
        {
            Index = 0,
            Delta = string.Empty,
            Partial = new Message { Role = "assistant" },
        };

    private static async IAsyncEnumerable<T> AsyncEnum<T>(T item)
    {
        await Task.Yield();
        yield return item;
    }

    private static async IAsyncEnumerable<T> Throwing<T>(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task Consume(IAsyncEnumerable<AssistantMessageEvent> stream)
    {
        await foreach (var _ in stream) { }
    }
}

internal sealed class CountingCircuitBreaker : ICircuitBreaker
{
    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }

    public CircuitState State => CircuitState.Closed;

    public bool TryAcquire() => true;

    public void RecordSuccess() => SuccessCount++;

    public void RecordFailure() => FailureCount++;

    public ProviderHealth GetHealth() => new() { State = State };
}

internal sealed class AlwaysOpenCircuitBreaker : ICircuitBreaker
{
    public CircuitState State => CircuitState.Open;

    public bool TryAcquire() => false;

    public void RecordSuccess() { }

    public void RecordFailure() { }

    public ProviderHealth GetHealth() => new() { State = State };
}

internal sealed class UnreachableLLmClient : ILLmClient
{
    public IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        CancellationToken ct
    ) => throw new InvalidOperationException("primary should not be called after failover");
}
