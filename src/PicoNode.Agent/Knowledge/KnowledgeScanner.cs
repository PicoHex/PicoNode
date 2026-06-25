namespace PicoNode.Agent;

public sealed class SkillInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class KnowledgeScanner
{
    public List<SkillInfo> Scan(string root)
    {
        var skills = new List<SkillInfo>();
        var knowledgeDir = Path.Combine(root, "knowledge");
        if (!Directory.Exists(knowledgeDir)) return skills;

        // Find all SKILL.md files recursively under knowledge/
        var skillFiles = Directory.GetFiles(knowledgeDir, "SKILL.md", SearchOption.AllDirectories);

        foreach (var skillPath in skillFiles)
        {
            var content = File.ReadAllText(skillPath);
            var skill = ParseSkillMarkdown(content);
            if (skill != null)
            {
                skill.Path = skillPath;
                skills.Add(skill);
            }
        }

        return skills.OrderBy(s => s.Name).ToList();
    }

    private static SkillInfo? ParseSkillMarkdown(string content)
    {
        var lines = content.Replace("\r", "").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return null;

        var skill = new SkillInfo();
        bool inFrontmatter = true;  // First --- already skipped by Skip(1)

        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed == "---")
            {
                if (!inFrontmatter) { inFrontmatter = true; continue; }
                break;
            }

            if (!inFrontmatter) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;

            var key = trimmed[..colonIdx].Trim();
            var value = trimmed[(colonIdx + 1)..].Trim();

            if (key == "name") skill.Name = value;
            else if (key == "description") skill.Description = value;
        }

        return string.IsNullOrEmpty(skill.Name) ? null : skill;
    }

    public static string BuildSkillsPrompt(List<SkillInfo> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<available_skills>");
        foreach (var skill in skills)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{skill.Name}</name>");
            sb.AppendLine($"    <description>{skill.Description}</description>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        return sb.ToString();
    }

    public SkillInfo? ParseForTest(string content) => ParseSkillMarkdown(content);
}
