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
            () => new DomainAgent(new FakeLlmClient(), new FakeToolRunner())
        );
        return (system, null!);
    }

    /// <summary>
    /// Stateless helper that sends the correct command sequence to update an agent's config.
    /// Extracted from Server.cs /api/config to be testable.
    /// </summary>
    public static void ApplyConfigCommands(
        DomainAgent agent,
        ActorSystem system,
        AgentConfig newConfig
    )
    {
        // --- current buggy logic (will fix after test fails) ---
        // This replicates the logic from Server.cs /api/config endpoint.
        // Phase 1: Add all new LLMs first (don't remove old ones yet)
        var newProviderNames = newConfig.Providers.Keys.ToList();

        // Track which providers already exist (will have duplicates after Phase 1)
        var duplicateProviders = newConfig
            .Providers.Keys.Where(name => agent.LlmsSnapshot.Any(l => l.ProviderName == name))
            .ToList();

        foreach (var (name, entry) in newConfig.Providers)
        {
            var llm = new Llm
            {
                ProviderName = name,
                ModelId = newConfig.Model ?? name,
                ApiKey = entry.ApiKey,
                BaseUrl = entry.BaseUrl ?? "",
            };
            system.Send(agent.Id, new AddLlmCmd(llm));
        }

        // Phase 2: Remove old providers not in new config
        var toRemove = agent
            .LlmsSnapshot.Where(l => !newProviderNames.Contains(l.ProviderName))
            .ToList();

        var newCurrent = newProviderNames.FirstOrDefault() ?? agent.LlmsSnapshot[0].ProviderName;
        var newModel = newConfig.Model ?? newCurrent;

        // Phase 3: Switch away from CurrentLlm if it's being removed
        if (
            toRemove.Any(r =>
                r.ProviderName == agent.CurrentLlm.ProviderName
                && r.ModelId == agent.CurrentLlm.ModelId
            )
        )
            system.Send(agent.Id, new SwitchLlmCmd(newCurrent, newModel));

        // Phase 4: Remove old providers.
        // Phase 3 already sent SwitchLlmCmd for CurrentLlm if needed,
        // so any removal is safe (commands processed in mailbox order).
        foreach (var old in toRemove)
        {
            system.Send(agent.Id, new RemoveLlmCmd(old.ProviderName, old.ModelId));
        }

        // Phase 5: Switch to desired current provider/model
        system.Send(agent.Id, new SwitchLlmCmd(newCurrent, newModel));

        // Phase 6: Remove old duplicate entries for providers that were updated.
        // Pre-computed before any commands — after Phase 5's SwitchLlmCmd (.Last())
        // the CurrentLlm is the new entry, so .First() removal picks the old one safely.
        foreach (var name in duplicateProviders)
        {
            system.Send(agent.Id, new RemoveLlmCmd(name, newConfig.Model ?? name));
        }
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

        ApplyConfigCommands(agent, system, newConfig);
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

        ApplyConfigCommands(agent, system, newConfig);
        await Task.Delay(100);

        // Assert: switched to openai with new key
        await Assert.That(agent.CurrentLlm.ProviderName).IsEqualTo("openai");
        await Assert.That(agent.CurrentLlm.ApiKey).IsEqualTo(NewKey);
        await Assert.That(agent.LlmsSnapshot.Count).IsEqualTo(1);
    }
}
