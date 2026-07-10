using System.Text;

namespace PicoNode.Agent.Domain;

public static class SystemPromptBuilder
{
    public static string Build(
        IReadOnlyList<Tool> tools,
        List<SkillInfo>? skills = null,
        string? baseDir = null)
    {
        var sb = new StringBuilder();

        if (skills is { Count: > 0 })
            sb.AppendLine(SkillFormatter.FormatSkillsPrompt(skills, baseDir));

        sb.AppendLine("You are PicoAgent, an AI coding assistant running on the PicoNode framework.");
        sb.AppendLine();
        sb.AppendLine("## Available Tools");
        sb.AppendLine();
        foreach (var t in tools)
        {
            sb.AppendLine($"- **{t.Name}**: {t.Description}");
        }
        sb.AppendLine();
        sb.AppendLine("Use tools when needed. Explain what you're doing before using a tool.");
        return sb.ToString();
    }
}
