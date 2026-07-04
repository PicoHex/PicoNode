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

    public string FormatForSystemPrompt()
    {
        var sb = new StringBuilder();
        foreach (var t in GetActiveTools())
        {
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);
            if (t.InputSchema is { Length: > 0 } schema)
            {
                try
                {
                    var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(schema));
                    if (doc.RootElement.TryGetProperty("properties", out var props))
                    {
                        var required = new HashSet<string>();
                        if (doc.RootElement.TryGetProperty("required", out var req))
                        {
                            for (int i = 0; i < req.GetArrayLength(); i++)
                                required.Add(req[i].GetString()!);
                        }
                        foreach (var prop in props.EnumerateObject())
                        {
                            var type = "string";
                            if (prop.Value.TryGetProperty("type", out var tProp))
                                type = tProp.GetString() ?? "string";
                            var desc = "";
                            if (prop.Value.TryGetProperty("description", out var dProp))
                                desc = dProp.GetString() ?? "";
                            sb.Append("    ").Append(prop.Name).Append(" (").Append(type);
                            if (required.Contains(prop.Name))
                                sb.Append(", required");
                            sb.Append(')');
                            if (desc.Length > 0)
                                sb.Append(": ").Append(desc);
                            sb.AppendLine();
                        }
                    }
                }
                catch (FormatException)
                {
                    // Schema parse failed — include raw
                    sb.Append("    schema: ").AppendLine(schema);
                }
            }
        }
        return sb.ToString();
    }

    public IReadOnlyList<string> GetEnabledNames() => _enabled.ToList();

    public ToolSchema[] GetActiveToolSchemas() =>
        GetActiveTools()
            .Where(t => t.InputSchema is { Length: > 0 })
            .Select(t => new ToolSchema
            {
                Function = new ToolSchemaFunction
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.InputSchema!,
                },
            })
            .ToArray();
}
