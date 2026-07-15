namespace PicoNode.Agent.Domain;

public static class SystemPromptBuilder
{
    private const string Identity = "You are PicoAgent, an AI coding assistant.";
    private const string ToolsHeader = "## Available Tools";
    private const string LanguageRule =
        "Always reply in the same language the user used. "
        + "Think and respond in the user's language.";
    private const string UsageInstruction =
        "Use tools when needed. Before running a tool, tell the user which tool you're using and why.";

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

        sb.AppendLine(Identity);
        sb.AppendLine();
        sb.AppendLine(ToolsHeader);
        sb.AppendLine();
        foreach (var t in tools)
            sb.AppendLine($"- **{t.Name}**: {t.Description}");
        sb.AppendLine();
        sb.AppendLine(LanguageRule);
        sb.AppendLine();
        sb.AppendLine(UsageInstruction);
        return sb.ToString();
    }
}
