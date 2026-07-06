namespace PicoNode.Tests;

public class PackageScanTests
{
    [Test]
    public async Task ScanSkills_WithPackageEntries_IncludesGitSkills()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico_scan_" + Guid.NewGuid().ToString("N")[..8]
        );
        var gitSkillsDir = Path.Combine(
            tmp,
            "git",
            "github.com",
            "test",
            "repo",
            "skills",
            "test-skill"
        );
        Directory.CreateDirectory(gitSkillsDir);
        await File.WriteAllTextAsync(
            Path.Combine(gitSkillsDir, "SKILL.md"),
            "---\nname: git-skill\ndescription: A git skill\n---\n\n# Content\n"
        );
        try
        {
            var entries = new List<PackageEntry>
            {
                new()
                {
                    DisplayPath = Path.Combine(tmp, "git", "github.com", "test", "repo"),
                    IsGit = true,
                },
            };
            var skills = AgentBuilder.ScanSkills(tmp, entries);
            var found = skills.Any(s => s.Name == "git-skill");
            await Assert.That(found).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task ScanSkills_NullPackages_BackwardCompat()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico_scan_" + Guid.NewGuid().ToString("N")[..8]
        );
        var skillsDir = Path.Combine(tmp, "skills", "local-skill");
        Directory.CreateDirectory(skillsDir);
        await File.WriteAllTextAsync(
            Path.Combine(skillsDir, "SKILL.md"),
            "---\nname: local-skill\ndescription: A local skill\n---\n\n# Content\n"
        );
        try
        {
            var skills = AgentBuilder.ScanSkills(tmp, null);
            var found = skills.Any(s => s.Name == "local-skill");
            await Assert.That(found).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task ScanSkills_DuplicateName_FirstWins()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico_dup_" + Guid.NewGuid().ToString("N")[..8]);
        // Package skill (higher priority)
        var pkgSkills = Path.Combine(tmp, "git", "gh", "o", "r", "skills", "dup");
        Directory.CreateDirectory(pkgSkills);
        await File.WriteAllTextAsync(
            Path.Combine(pkgSkills, "SKILL.md"),
            "---\nname: dup\ndescription: Package version\n---\n\n# Pkg\n"
        );
        // Manual skill (lower priority, same name)
        var manualSkills = Path.Combine(tmp, "skills", "dup");
        Directory.CreateDirectory(manualSkills);
        await File.WriteAllTextAsync(
            Path.Combine(manualSkills, "SKILL.md"),
            "---\nname: dup\ndescription: Manual version\n---\n\n# Manual\n"
        );
        try
        {
            var entries = new List<PackageEntry>
            {
                new() { DisplayPath = Path.Combine(tmp, "git", "gh", "o", "r") },
            };
            var skills = AgentBuilder.ScanSkills(tmp, entries);
            var dup = skills.First(s => s.Name == "dup");
            // First in list = package skills (higher priority) → should win
            await Assert.That(dup.Description).IsEqualTo("Package version");
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }
}
