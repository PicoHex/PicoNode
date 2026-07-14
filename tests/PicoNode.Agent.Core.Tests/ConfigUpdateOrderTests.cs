using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: /api/config must send SwitchLlmCmd BEFORE RemoveLlmCmd
/// when updating an existing provider with a new API key.
/// </summary>
public sealed class ConfigUpdateOrderTests
{
    private const string NewKey = "sk-new-123456";
    private const string OldKey = "sk-old-abcdef";

    private static (ActorSystem System, DomainAgent Agent) CreateSystem()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent()
        );
        return (system, null!);
    }

    [Test]
    public async Task UpdateSingleProvider_NewApiKey_ShouldUseNewKey()
    {
        // Arrange: agent with one provider using OldKey
        var (system, _) = CreateSystem();
        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "deepseek",
                        ModelId = "v4",
                        ApiKey = OldKey,
                    },
                ],
                "deepseek",
                "v4",
                Guid.CreateVersion7()
            )
        );
        await Task.Delay(50);

        await Assert.That(agent.CurrentLlm.ApiKey).IsEqualTo(OldKey);

        // Act: apply config with new key for same provider
        var newConfig = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["deepseek"] = new() { ApiKey = NewKey, BaseUrl = "https://api.deepseek.com/v1" },
            },
            Model = "v4",
        };

        ConfigApplier.Apply(agent, system, newConfig);
        await Task.Delay(100);

        // Assert: CurrentLlm should use NewKey — THIS IS THE TEST THAT SHOULD FAIL
        // with the buggy ordering because:
        // 1. RemoveLlmCmd("deepseek","v4") is sent first → throws (is CurrentLlm)
        // 2. AddLlmCmd never executes
        // 3. CurrentLlm still has OldKey
        await Assert.That(agent.CurrentLlm.ApiKey).IsEqualTo(NewKey);
        await Assert.That(agent.LlmsSnapshot.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateSingleProvider_ToDifferentProvider_ShouldSwitch()
    {
        // Arrange: agent with deepseek
        var (system, _) = CreateSystem();
        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "deepseek",
                        ModelId = "v4",
                        ApiKey = OldKey,
                    },
                ],
                "deepseek",
                "v4",
                Guid.CreateVersion7()
            )
        );
        await Task.Delay(50);

        // Act: switch to a completely new provider
        var newConfig = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["openai"] = new() { ApiKey = NewKey, BaseUrl = "https://api.openai.com/v1" },
            },
            Model = "gpt-4",
        };

        ConfigApplier.Apply(agent, system, newConfig);
        await Task.Delay(100);

        // Assert: switched to openai with new key
        await Assert.That(agent.CurrentLlm.ProviderName).IsEqualTo("openai");
        await Assert.That(agent.CurrentLlm.ApiKey).IsEqualTo(NewKey);
        await Assert.That(agent.LlmsSnapshot.Count).IsEqualTo(1);
    }

    /// <summary>
    /// BUG: when an existing provider is updated with a DIFFERENT model, Phase 6
    /// sent RemoveLlmCmd(name, newModel) — which after Phase 5 matches the new
    /// CurrentLlm and trips the "Cannot remove CurrentLlm" invariant. The
    /// exception was silently swallowed by fire-and-forget Send, so the stale old
    /// entry was never removed and LlmsSnapshot kept two entries.
    /// Desired: the old (provider, oldModel) entry is removed, leaving exactly one.
    /// </summary>
    [Test]
    public async Task UpdateExistingProvider_NewModel_RemovesOldEntryLeavingOne()
    {
        var (system, _) = CreateSystem();
        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "openai",
                        ModelId = "old-model",
                        ApiKey = OldKey,
                    },
                ],
                "openai",
                "old-model",
                Guid.CreateVersion7()
            )
        );
        await Task.Delay(50);

        var newConfig = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["openai"] = new() { ApiKey = NewKey, BaseUrl = "https://api.openai.com/v1" },
            },
            Model = "new-model",
        };

        ConfigApplier.Apply(agent, system, newConfig);
        await Task.Delay(200);

        // The old entry must be gone — exactly one Llm remains.
        await Assert.That(agent.LlmsSnapshot.Count).IsEqualTo(1);
        // Current is the new model with the new key.
        await Assert.That(agent.CurrentLlm.ModelId).IsEqualTo("new-model");
        await Assert.That(agent.CurrentLlm.ApiKey).IsEqualTo(NewKey);
    }
}
