namespace PicoNode.Agent;

public static class SkillFormatter
{
    public static string FormatSkillsPrompt(List<SkillInfo> skills)
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        if (visible.Count == 0)
            return $"No skills installed. To install: `git clone <url> {FileSystemConstants.GitDir}/<host>/<owner>/<repo>/` then POST /reload.";

        var sb = new StringBuilder();
        sb.AppendLine("## Skill Installation");
        sb.AppendLine();
        sb.AppendLine("Skills are discovered from two locations:");
        sb.AppendLine();
        sb.AppendLine(
            $"1. **Cloned repos** — `{FileSystemConstants.GitDir}/<host>/<owner>/<repo>/`"
        );
        sb.AppendLine(
            $"   Use `git clone <url> {FileSystemConstants.GitDir}/<host>/<owner>/<repo>/` to install."
        );
        sb.AppendLine(
            $"   Example: `git clone https://github.com/anthropics/skills.git {FileSystemConstants.GitDir}/github.com/anthropics/skills/`"
        );
        sb.AppendLine(
            $"   The repo's `{FileSystemConstants.SkillsDir}/` directory is automatically scanned for SKILL.md files."
        );
        sb.AppendLine($"   Run `/reload` (POST /reload) after cloning to discover new skills.");
        sb.AppendLine();
        sb.AppendLine(
            $"2. **Standalone skills** — `{FileSystemConstants.SkillsDir}/<skill-name>/SKILL.md`"
        );
        sb.AppendLine(
            "   For creating a new local skill, write a single SKILL.md into a new subdirectory."
        );
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
