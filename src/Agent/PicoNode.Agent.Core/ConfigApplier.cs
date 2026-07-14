using PicoNode.Actor;
using PicoNode.AI;

namespace PicoNode.Agent.Domain;

/// <summary>
/// Applies an <see cref="AgentConfig"/> to a live <see cref="Agent"/> by sending
/// the correct sequence of commands through the <see cref="ActorSystem"/>. This is
/// the single source of truth for the /api/config save logic; the Server endpoint
/// delegates here so the ordering is unit-testable.
/// </summary>
public static class ConfigApplier
{
    public static void Apply(Agent agent, ActorSystem system, AgentConfig newConfig)
    {
        var newProviderNames = newConfig.Providers.Keys.ToList();

        // Capture the OLD entries (provider + model) for providers that already
        // exist, BEFORE Phase 1 adds the new ones. Phase 6 must remove these stale
        // duplicates by their OLD model — removing by newModel would target the
        // new entry (CurrentLlm after Phase 5) and trip the "Cannot remove
        // CurrentLlm" invariant, whose exception fire-and-forget Send swallows.
        var oldDuplicates = agent
            .LlmsSnapshot.Where(l => newProviderNames.Contains(l.ProviderName))
            .Select(l => (l.ProviderName, l.ModelId))
            .ToList();

        // Phase 1: Add all new LLMs first (don't remove old ones yet)
        foreach (var (name, entry) in newConfig.Providers)
        {
            var llm = new Llm
            {
                ProviderName = name,
                ModelId = newConfig.Model ?? name,
                ApiKey = entry.ApiKey,
                BaseUrl = entry.BaseUrl ?? "",
                ApiFormat = (entry.ApiFormat?.ToLowerInvariant()) switch
                {
                    "anthropic" => AiApiFormat.AnthropicMessages,
                    _ => AiApiFormat.OpenAIChatCompletions,
                },
                ThinkingLevel =
                    AgentConfig.ParseLevel(newConfig.ThinkingLevel)
                    ?? AgentConfig.DefaultThinkingLevel,
                MaxTokens = newConfig.MaxTokens ?? 4096,
                ThinkingEnabled = newConfig.ThinkingEnabled,
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

        // Phase 4: Remove old providers
        foreach (var old in toRemove)
        {
            system.Send(agent.Id, new RemoveLlmCmd(old.ProviderName, old.ModelId));
        }

        // Phase 5: Switch to desired current provider/model
        system.Send(agent.Id, new SwitchLlmCmd(newCurrent, newModel));

        // Phase 6: Remove the OLD duplicate entries for providers that were
        // updated, keyed by their OLD model. SwitchLlmCmd uses .Last(), so after
        // Phase 5 the CurrentLlm is the newly-added entry; the old entry (matched
        // by FirstOrDefault on the old model) is never CurrentLlm and is safe to
        // remove — whether the model changed or stayed the same.
        foreach (var (name, oldModel) in oldDuplicates)
        {
            system.Send(agent.Id, new RemoveLlmCmd(name, oldModel));
        }
    }
}
