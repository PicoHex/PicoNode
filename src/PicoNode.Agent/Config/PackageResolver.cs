namespace PicoNode.Agent;

public sealed class PackageEntry
{
    /// <summary>Path where the package lives on disk.</summary>
    public string DisplayPath { get; set; } = string.Empty;

    /// <summary>Git clone URL (https://). Null for local packages.</summary>
    public string? GitUrl { get; set; }

    /// <summary>Optional ref (branch/tag) to checkout after clone.</summary>
    public string? GitRef { get; set; }

    /// <summary>True if this is a git: package.</summary>
    public bool IsGit { get; set; }
}

public static class PackageResolver
{
    private const string GitPrefix = "git:";
    private const string GitDirName = "git";

    /// <summary>
    /// Parse package entries into resolved <see cref="PackageEntry"/> objects.
    /// Does NOT perform git clone — that's handled by <see cref="PackageInstaller"/>.
    /// Malformed entries are silently skipped.
    /// </summary>
    public static List<PackageEntry> Resolve(string homeDir, List<string>? packages)
    {
        if (packages is null || packages.Count == 0)
            return [];

        var results = new List<PackageEntry>();
        foreach (var entry in packages)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (entry.StartsWith(GitPrefix, StringComparison.Ordinal))
            {
                var parsed = ParseGitEntry(homeDir, entry);
                if (parsed is not null)
                    results.Add(parsed);
            }
            else
            {
                var localPath = Path.GetFullPath(Path.Combine(homeDir, entry));
                if (Directory.Exists(localPath))
                    results.Add(new PackageEntry { DisplayPath = localPath, IsGit = false });
            }
        }

        return results;
    }

    /// <summary>
    /// Parse a git: entry like "github.com/owner/repo" or "github.com/owner/repo@v1".
    /// Only the first three path segments (host/owner/repo) are used;
    /// deeper paths are ignored. Returns null for malformed entries.
    /// </summary>
    private static PackageEntry? ParseGitEntry(string homeDir, string entry)
    {
        var path = entry[GitPrefix.Length..];
        string? gitRef = null;
        var atIdx = path.IndexOf('@');
        if (atIdx >= 0)
        {
            gitRef = path[(atIdx + 1)..];
            path = path[..atIdx];
        }

        var parts = path.Split('/');
        if (parts.Length < 3)
            return null;

        var host = parts[0];
        var owner = parts[1];
        var repo = parts[2];

        var displayPath = Path.Combine(homeDir, GitDirName, host, owner, repo);
        var gitUrl = $"https://{host}/{owner}/{repo}.git";

        return new PackageEntry
        {
            DisplayPath = displayPath,
            GitUrl = gitUrl,
            GitRef = gitRef,
            IsGit = true,
        };
    }
}
