namespace PicoNode.Agent;

public static class SystemPromptBuilder
{
    public static string FormatToolsPrompt(IReadOnlyList<ManifestCapability> capabilities)
    {
        if (capabilities.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("Available tools:");
        foreach (var cap in capabilities)
        {
            sb.AppendLine($"- {cap.Name}: {cap.Description}");
            if (cap.Guidelines is { Length: > 0 } g)
                sb.AppendLine($"  ({g})");
        }
        return sb.ToString();
    }

    public static string Build(
        IReadOnlyList<SkillInfo> skills,
        IReadOnlyList<ManifestCapability> tools,
        string? agentsMd = null)
    {
        var sb = new StringBuilder();
        if (agentsMd is not null)
            sb.AppendLine(agentsMd).AppendLine();
        sb.AppendLine(SkillFormatter.FormatSkillsPrompt(skills.ToList()));
        sb.AppendLine(FormatToolsPrompt(tools));
        return sb.ToString();
    }
}
