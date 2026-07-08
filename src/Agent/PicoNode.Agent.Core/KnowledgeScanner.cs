namespace PicoNode.Agent.Domain;

public sealed class SkillInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool DisableModelInvocation { get; set; }
}

public sealed class SkillSource
{
    public string Path { get; init; } = string.Empty;
    public string SourceTag { get; init; } = string.Empty;
}

public sealed class KnowledgeScanner
{
    private static readonly Regex ValidNameRegex = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled
    );
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    public List<SkillInfo> Scan(string root)
    {
        var knowledgeDir = Path.Combine(root, FileSystemConstants.KnowledgeDir);
        if (!Directory.Exists(knowledgeDir))
            return [];

        return ScanDirectory(knowledgeDir);
    }

    public List<SkillInfo> Scan(IEnumerable<SkillSource> sources)
    {
        var seen = new HashSet<string>();
        var skills = new List<SkillInfo>();

        foreach (var source in sources)
        {
            if (!Directory.Exists(source.Path))
                continue;
            var dirSkills = ScanDirectory(source.Path);
            foreach (var skill in dirSkills)
            {
                if (seen.Add(skill.Name))
                    skills.Add(skill);
            }
        }
        return skills.OrderBy(s => s.Name).ToList();
    }

    public List<SkillInfo> ScanFromDir(string dir)
    {
        if (!Directory.Exists(dir))
            return [];
        return ScanDirectory(dir);
    }

    private static List<SkillInfo> ScanDirectory(string dir)
    {
        var skills = new List<SkillInfo>();
        var skillFiles = Directory.GetFiles(
            dir,
            FileSystemConstants.SkillFile,
            SearchOption.AllDirectories
        );

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
        return skills;
    }

    private static SkillInfo? ParseSkillMarkdown(string content)
    {
        var lines = content.Replace("\r", "").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != YmlConstants.FrontmatterDelim)
            return null;

        var skill = new SkillInfo();
        bool inFrontmatter = true;

        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed == YmlConstants.FrontmatterDelim)
            {
                if (!inFrontmatter)
                {
                    inFrontmatter = true;
                    continue;
                }
                break;
            }

            if (!inFrontmatter)
                continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0)
                continue;

            var key = trimmed[..colonIdx].Trim();
            var value = trimmed[(colonIdx + 1)..].Trim();

            if (key == YmlConstants.KeyName)
                skill.Name = value;
            else if (key == YmlConstants.KeyDescription)
                skill.Description = value;
            else if (key == "disable-model-invocation")
                skill.DisableModelInvocation = string.Equals(
                    value,
                    "true",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        // Validation
        if (string.IsNullOrWhiteSpace(skill.Name))
            return null;
        if (!ValidNameRegex.IsMatch(skill.Name) || skill.Name.Length > MaxNameLength)
            return null;
        if (
            string.IsNullOrWhiteSpace(skill.Description)
            || skill.Description.Length > MaxDescriptionLength
        )
            return null;

        return skill;
    }

    public SkillInfo? ParseForTest(string content) => ParseSkillMarkdown(content);
}
