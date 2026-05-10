using System.Xml.Linq;

namespace PicoNode.Tests;

public sealed class PackageReferenceTests
{
    private static string SlnRoot => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..");

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

    [Test]
    public async Task PicoNode_csproj_has_PicoLog_Abs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoNode\PicoNode.csproj");
        await Assert.That(refs).Contains("PicoLog.Abs");
    }

    [Test]
    public async Task PicoNode_Http_csproj_has_PicoLog_Abs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoNode.Http\PicoNode.Http.csproj");
        await Assert.That(refs).Contains("PicoLog.Abs");
    }

    [Test]
    public async Task PicoNode_Web_csproj_has_PicoDI_Abs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoNode.Web\PicoNode.Web.csproj");
        await Assert.That(refs).Contains("PicoDI.Abs");
    }

    [Test]
    public async Task PicoNode_Web_csproj_has_PicoLog_Abs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoNode.Web\PicoNode.Web.csproj");
        await Assert.That(refs).Contains("PicoLog.Abs");
    }

    [Test]
    public async Task PicoNode_Web_csproj_has_PicoCfg_Abs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoNode.Web\PicoNode.Web.csproj");
        await Assert.That(refs).Contains("PicoCfg.Abs");
    }

    [Test]
    public async Task PicoWeb_csproj_has_PicoCfg_Abs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoWeb\PicoWeb.csproj");
        await Assert.That(refs).Contains("PicoCfg.Abs");
    }

    [Test]
    public async Task PicoNode_Abs_csproj_has_zero_PicoHex_refs()
    {
        var refs = GetPicoHexPackageRefs(@"src\PicoNode.Abs\PicoNode.Abs.csproj");
        await Assert.That(refs.Length).IsEqualTo(0);
    }
}
