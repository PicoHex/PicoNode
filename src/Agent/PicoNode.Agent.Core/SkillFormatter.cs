namespace PicoNode.Agent.Domain;

public static class SkillFormatter
{
    public static string FormatSkillsPrompt(List<SkillInfo> skills, string? baseDir = null)
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        var skillsDir = baseDir is not null
            ? Path.Combine(baseDir, FileSystemConstants.SkillsDir)
            : FileSystemConstants.SkillsDir;

        if (visible.Count == 0)
            return $"No skills installed. Add entries to `packages` in settings.json (e.g. \"git:github.com/anthropics/skills\") or `git clone <url> {skillsDir}/<host>/<owner>/<repo>/`. Then POST /reload.";

        var sb = new StringBuilder();
        sb.AppendLine("## Skill Installation");
        sb.AppendLine();
        sb.AppendLine("Skills are discovered from three sources (highest priority first):");
        sb.AppendLine();
        sb.AppendLine(
            $"1. **Project skills** — `{FileSystemConstants.ProjectSkillsDir}/` relative to the working directory."
        );
        sb.AppendLine();
        sb.AppendLine("2. **Packages** — entries in the `packages` field of `settings.json`:");
        sb.AppendLine(
            $"   - `git:<host>/<owner>/<repo>` — auto-cloned to `{skillsDir}/<host>/<owner>/<repo>/` on startup."
        );
        sb.AppendLine($"   - `<relative-path>` — local directory relative to the home directory.");
        sb.AppendLine(
            $"   Example packages: [\"git:github.com/anthropics/skills\", \"local-src/my-tools\"]"
        );
        sb.AppendLine("   Run `/reload` after editing `packages` to discover new skills.");
        sb.AppendLine();
        sb.AppendLine(
            $"3. **Manual skills** — `{skillsDir}/<skill-name>/SKILL.md` placed directly, or"
        );
        sb.AppendLine(
            $"   `git clone <url> {skillsDir}/<host>/<owner>/<repo>/` for quick testing."
        );
        sb.AppendLine();
        sb.AppendLine("For remote skill repos, always add them to `packages` in `settings.json`");
        sb.AppendLine("rather than manually copying files. This preserves the full repository");
        sb.AppendLine("structure, enables `git pull` updates, and keeps skills organized.");

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
