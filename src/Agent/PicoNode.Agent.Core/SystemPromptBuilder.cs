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
            sb.AppendLine($"- **{t.Name}**: {t.Description}");
        sb.AppendLine();
        sb.AppendLine("## Installing Skills");
        sb.AppendLine();
        sb.AppendLine("To install a skill from GitHub:");
        sb.AppendLine("1. Edit `settings.json` and add the repo to the `packages` array:");
        sb.AppendLine("   `\"packages\": [\"git:github.com/owner/repo\"]`");
        sb.AppendLine("2. Then call POST `/reload` to discover new skills.");
        sb.AppendLine("3. Or use the `write` tool to edit `settings.json` directly, then POST `/api/reload`.");
        sb.AppendLine();
        sb.AppendLine("Use tools when needed. Before running a tool, tell the user which tool you're using and why.");
        return sb.ToString();
    }
}
