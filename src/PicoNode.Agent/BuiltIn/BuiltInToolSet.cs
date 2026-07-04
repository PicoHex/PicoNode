namespace PicoNode.Agent;

public enum ToolPreset { Coding, ReadOnly, All }

public sealed class BuiltInToolSet
{
    private readonly Dictionary<string, IBuiltInTool> _all = new();
    private readonly HashSet<string> _enabled = new();

    public void Register(IBuiltInTool tool)
    {
        _all[tool.Name] = tool;
        _enabled.Add(tool.Name);
    }

    public void ApplyPreset(ToolPreset preset)
    {
        _enabled.Clear();
        var names = preset switch
        {
            ToolPreset.Coding => new[] { "read", "bash", "edit", "write" },
            ToolPreset.ReadOnly => new[] { "read", "grep", "find", "ls" },
            ToolPreset.All => _all.Keys.ToArray(),
            _ => Array.Empty<string>(),
        };
        foreach (var n in names)
        {
            if (_all.ContainsKey(n))
                _enabled.Add(n);
        }
    }

    public void Enable(string name)
    {
        if (_all.ContainsKey(name))
            _enabled.Add(name);
    }

    public void Disable(string name) => _enabled.Remove(name);

    public IBuiltInTool? Find(string name) =>
        _enabled.Contains(name) && _all.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<IBuiltInTool> GetActiveTools() =>
        _enabled.Select(n => _all.GetValueOrDefault(n)).Where(t => t is not null)!;

    public string FormatForSystemPrompt() =>
        string.Join('\n', GetActiveTools().Select(t => $"- {t.Name}: {t.Description}"));

    public IReadOnlyList<string> GetEnabledNames() => _enabled.ToList();
}
