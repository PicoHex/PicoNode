namespace PicoNode.Agent.Domain;

public static class SystemPromptBuilder
{
    public static string Build(
        IReadOnlyList<Tool> tools,
        List<SkillInfo>? skills = null,
        string? baseDir = null
    )
    {
        var sb = new StringBuilder();

        if (skills is not null)
        {
            sb.AppendLine(SkillFormatter.FormatSkillsPrompt(skills, baseDir));
            sb.AppendLine();
        }

        sb.AppendLine("You are PicoAgent, an AI coding assistant.");
        sb.AppendLine();
        sb.AppendLine("## Available Tools");
        sb.AppendLine();
        foreach (var t in tools)
            sb.AppendLine($"- **{t.Name}**: {t.Description}");
        sb.AppendLine();
        sb.AppendLine(
            "Always reply in the same language the user used. "
                + "Think and respond in the user's language."
        );
        sb.AppendLine();
        sb.AppendLine(
            "Use tools when needed. Before running a tool, tell the user which tool you're using and why."
        );
        return sb.ToString();
    }
}
