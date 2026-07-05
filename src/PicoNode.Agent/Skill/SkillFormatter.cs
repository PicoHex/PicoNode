namespace PicoNode.Agent;

public static class SkillFormatter
{
    public static string FormatSkillsPrompt(List<SkillInfo> skills)
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        var sb = new StringBuilder();
        sb.Append("Skills are stored in ").Append(FileSystemConstants.SkillsDir).AppendLine("/.");
        sb.AppendLine("To install: git clone <url> for remote repos, or copy the folder for local skills.");
        if (visible.Count == 0)
            return sb.ToString();

        sb.AppendLine();
        sb.AppendLine("The following skills are available. To use a skill, read its file with the read tool at the location shown.");
        sb.AppendLine();
        sb.AppendLine("<available_skills>");
        foreach (var skill in visible)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.Path)}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        return sb.ToString();
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string FormatSkillInvocation(SkillInfo skill, string fullContent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"skill: {skill.Name}");
        sb.AppendLine($"location: {skill.Path}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(fullContent);
        return sb.ToString();
    }
}
