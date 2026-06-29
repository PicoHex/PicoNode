namespace PicoNode.Agent;

public static class SkillFormatter
{
    public static string FormatSkillsPrompt(List<SkillInfo> skills)
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        if (visible.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("skills:");
        foreach (var skill in visible)
        {
            sb.AppendLine($"  - name: {skill.Name}");
            sb.AppendLine($"    description: {skill.Description}");
            sb.AppendLine($"    location: {skill.Path}");
        }
        sb.AppendLine("---");
        return sb.ToString();
    }

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
