
namespace PicoNode.Agent;

public sealed class HookRunner
{
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityRunner _runner;

    public HookRunner(CapabilityRegistry registry, CapabilityRunner runner)
    {
        _registry = registry;
        _runner = runner;
    }

    public async Task<JsonElement?> EmitAsync(
        TriggerKind trigger,
        byte[] input,
        CancellationToken ct
    )
    {
        var hooks = _registry.GetByTrigger(trigger).OrderBy(c => c.Priority);
        foreach (var hook in hooks)
        {
            var result = await _runner.ExecuteAsync(hook, "hook", input, ct);
            if (result.TryGetProperty("action", out var action) && action.GetString() == "block")
                return result;
        }
        return null;
    }
}
