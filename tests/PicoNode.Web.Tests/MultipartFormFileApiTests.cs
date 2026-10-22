namespace PicoNode.Web.Tests;

public sealed class MultipartFormFileApiTests
{
    [Test]
    public async Task OpenReadStream_returns_stream_with_file_content()
    {
        var data = "hello file content"u8.ToArray();
        var file = new MultipartFormFile("doc", "readme.txt", "text/plain", data);

        await Assert.That(file.Name).IsEqualTo("doc");
        await Assert.That(file.FileName).IsEqualTo("readme.txt");
        await Assert.That(file.ContentType).IsEqualTo("text/plain");
        await Assert.That(file.Length).IsEqualTo(data.Length);

        using var stream = file.OpenReadStream();
        var buffer = new byte[data.Length];
        var bytesRead = await stream.ReadAsync(buffer);
        await Assert.That(bytesRead).IsEqualTo(data.Length);
        await Assert.That(buffer).IsEquivalentTo(data);
    }

    // ── SafeFileName ────────────────────────────────────────────────────────

    [Test]
    public async Task SafeFileName_strips_traversal()
    {
        // Audit F-45: FileName is verbatim attacker text. A host that joins it onto
        // an upload directory writes to ../../win.ini.
        var file = new MultipartFormFile("doc", "..\\..\\win.ini", "text/plain", "x"u8.ToArray());

        await Assert.That(file.FileName).IsEqualTo("..\\..\\win.ini");
        await Assert.That(file.SafeFileName).IsEqualTo("win.ini");
    }

    [Test]
    public async Task SafeFileName_strips_forward_slash_traversal()
    {
        // '\\' is not a separator to Path.GetFileName on Unix, so the sanitiser
        // handles both characters itself.
        var file = new MultipartFormFile("doc", "/etc/passwd", "text/plain", "x"u8.ToArray());
        await Assert.That(file.SafeFileName).IsEqualTo("passwd");

        var nested = new MultipartFormFile("doc", "../../a/b.txt", "text/plain", "x"u8.ToArray());
        await Assert.That(nested.SafeFileName).IsEqualTo("b.txt");
    }

    [Test]
    public async Task SafeFileName_drops_control_characters_and_drive_qualifiers()
    {
        var nul = new MultipartFormFile("doc", "a\u0000b.txt", "text/plain", "x"u8.ToArray());
        await Assert.That(nul.SafeFileName).IsEqualTo("ab.txt");

        // "C:evil.txt" is drive-relative: Path.Combine(uploadDir, it) escapes uploadDir.
        var drive = new MultipartFormFile("doc", "C:evil.txt", "text/plain", "x"u8.ToArray());
        await Assert.That(drive.SafeFileName).IsEqualTo("evil.txt");

        var mixed = new MultipartFormFile(
            "doc",
            "C:\\tmp\\re\u0007port.txt",
            "text/plain",
            "x"u8.ToArray()
        );
        await Assert.That(mixed.SafeFileName).IsEqualTo("report.txt");
    }

    [Test]
    public async Task SafeFileName_refuses_dot_dot()
    {
        // Nothing usable is left, so the sanitiser refuses rather than guessing.
        foreach (var candidate in new[] { "..", ".", "a/..", "\\\\", "///" })
        {
            var file = new MultipartFormFile(
                "doc",
                candidate,
                "text/plain",
                ReadOnlyMemory<byte>.Empty
            );
            await Assert.That(file.SafeFileName).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task SafeFileName_keeps_a_plain_file_name()
    {
        var file = new MultipartFormFile("doc", "readme.txt", "text/plain", "x"u8.ToArray());
        await Assert.That(file.SafeFileName).IsEqualTo("readme.txt");

        var unicode = new MultipartFormFile("doc", "报告 v2.pdf", "application/pdf", "x"u8.ToArray());
        await Assert.That(unicode.SafeFileName).IsEqualTo("报告 v2.pdf");
    }
}
