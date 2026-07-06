namespace PicoNode.Agent;

public static class SkillFormatter
{
    public static string FormatSkillsPrompt(List<SkillInfo> skills, string? baseDir = null)
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        var skillsDir = baseDir is not null
            ? Path.Combine(baseDir, FileSystemConstants.SkillsDir)
            : FileSystemConstants.SkillsDir;

        if (visible.Count == 0)
            return $"No skills installed. To install: `git clone <url> {skillsDir}/<host>/<owner>/<repo>/` then POST /reload.";

        var sb = new StringBuilder();
        sb.AppendLine("## Skill Installation");
        sb.AppendLine();
        sb.AppendLine("All skills live under the `skills/` directory:");
        sb.AppendLine();
        sb.AppendLine($"- **Cloned repos**: `git clone <url> {skillsDir}/<host>/<owner>/<repo>/`");
        sb.AppendLine(
            $"  Example: `git clone https://github.com/anthropics/skills.git {skillsDir}/github.com/anthropics/skills/`"
        );
        sb.AppendLine($"- **Local skills**: create `{skillsDir}/<skill-name>/SKILL.md`");
        sb.AppendLine();
        sb.AppendLine($"Run `/reload` (POST /reload) after changes to discover new skills.");
        sb.AppendLine();
        sb.AppendLine(
            "For remote skill repos, always use `git clone` — this preserves the full repository"
        );
        sb.AppendLine(
            "structure, enables `git pull` updates, and keeps skills organized. Do not copy"
        );
        sb.AppendLine("individual SKILL.md files from a remote repo into the skills/ directory.");

        sb.AppendLine();
        sb.AppendLine(
            "The following skills are available. To use a skill, read its file with the read tool at the location shown."
        );
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
