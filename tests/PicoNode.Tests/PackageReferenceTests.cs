namespace PicoNode.Tests;

public sealed class PackageReferenceTests
{
    private static string SlnRoot =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    private static XDocument LoadCsproj(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(SlnRoot, relativePath));
        return XDocument.Load(path);
    }

    private static string[] GetPicoHexPackageRefs(string relativePath)
    {
        var doc = LoadCsproj(relativePath);
        return doc.Descendants("PackageReference")
            .Select(x => x.Attribute("Include")?.Value)
            .Where(v => v is not null && v.StartsWith("Pico"))
            .Select(v => v!)
            .ToArray();
    }

    /// <summary>
    /// PicoHex package refs reachable through the project-reference graph — the
    /// dependency set a consumer ultimately receives. A redundant direct
    /// reference may be pruned as long as the package stays reachable.
    /// </summary>
    private static string[] GetTransitivePicoHexPackageRefs(string relativePath)
    {
        var root = Path.GetFullPath(SlnRoot);
        var packageRefs = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(relativePath, root, packageRefs, visited);
        return [.. packageRefs];
    }

    private static void Walk(
        string relativePath,
        string root,
        HashSet<string> packageRefs,
        HashSet<string> visited
    )
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!visited.Add(fullPath))
            return;

        var doc = LoadCsproj(relativePath);
        foreach (var package in doc.Descendants("PackageReference"))
        {
            var id = package.Attribute("Include")?.Value;
            if (id is not null && id.StartsWith("Pico", StringComparison.Ordinal))
                packageRefs.Add(id);
        }

        var projectDirectory = Path.GetDirectoryName(fullPath)!;
        foreach (var project in doc.Descendants("ProjectReference"))
        {
            var include = project.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
                continue;

            var referenced = Path.GetFullPath(
                Path.Combine(projectDirectory, include.Replace('\\', Path.DirectorySeparatorChar))
            );
            Walk(Path.GetRelativePath(root, referenced), root, packageRefs, visited);
        }
    }

    [Test]
    public async Task PicoNode_csproj_has_PicoLog_Abs()
    {
        var refs = GetPicoHexPackageRefs("src/Network/PicoNode/PicoNode.csproj");
        await Assert.That(refs).Contains("PicoLog.Abs");
    }

    [Test]
    public async Task PicoNode_Http_csproj_has_PicoLog_Abs()
    {
        var refs = GetPicoHexPackageRefs("src/Http/PicoNode.Http/PicoNode.Http.csproj");
        await Assert.That(refs).Contains("PicoLog.Abs");
    }

    [Test]
    public async Task PicoNode_Web_csproj_has_PicoDI_Abs()
    {
        var refs = GetPicoHexPackageRefs("src/Web/PicoNode.Web/PicoNode.Web.csproj");
        await Assert.That(refs).Contains("PicoDI.Abs");
    }

    [Test]
    public async Task PicoNode_Web_csproj_has_PicoLog_Abs()
    {
        var refs = GetPicoHexPackageRefs("src/Web/PicoNode.Web/PicoNode.Web.csproj");
        await Assert.That(refs).Contains("PicoLog.Abs");
    }

    [Test]
    public async Task PicoNode_Web_reaches_PicoCfg_Abs_transitively()
    {
        // The direct reference was pruned as redundant (PicoNode.Http declares it);
        // it must stay reachable so consumers keep receiving the package.
        var refs = GetTransitivePicoHexPackageRefs("src/Web/PicoNode.Web/PicoNode.Web.csproj");
        await Assert.That(refs).Contains("PicoCfg.Abs");
    }

    [Test]
    public async Task PicoWeb_reaches_PicoCfg_Abs_transitively()
    {
        var refs = GetTransitivePicoHexPackageRefs("src/Web/PicoWeb/PicoWeb.csproj");
        await Assert.That(refs).Contains("PicoCfg.Abs");
    }

    [Test]
    public async Task PicoNode_Abs_csproj_has_zero_PicoHex_refs()
    {
        var refs = GetPicoHexPackageRefs("src/Network/PicoNode.Abs/PicoNode.Abs.csproj");
        await Assert.That(refs.Length).IsEqualTo(0);
    }
}
