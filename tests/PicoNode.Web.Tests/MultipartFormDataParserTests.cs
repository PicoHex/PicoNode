using System.Text;

namespace PicoNode.Web.Tests;

public sealed class MultipartFormDataParserTests
{
    private static HttpRequest CreateMultipartRequest(string boundary, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return new HttpRequest
        {
            Method = "POST",
            Target = "/upload",
            Version = "HTTP/1.1",
            HeaderFields =
            [
                new("Content-Type", $"multipart/form-data; boundary={boundary}"),
                new("Content-Length", bodyBytes.Length.ToString()),
                new("Host", "localhost"),
            ],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = $"multipart/form-data; boundary={boundary}",
                ["Content-Length"] = bodyBytes.Length.ToString(),
                ["Host"] = "localhost",
            },
            Body = bodyBytes,
        };
    }

    [Test]
    public async Task Parses_single_text_field()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"username\"\r\n"
            + "\r\n"
            + "alice\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("username");
        await Assert.That(result.Fields[0].Value).IsEqualTo("alice");
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parses_multiple_text_fields()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"first\"\r\n"
            + "\r\n"
            + "Alice\r\n"
            + "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"last\"\r\n"
            + "\r\n"
            + "Smith\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(2);
        await Assert.That(result.Fields[0].Name).IsEqualTo("first");
        await Assert.That(result.Fields[0].Value).IsEqualTo("Alice");
        await Assert.That(result.Fields[1].Name).IsEqualTo("last");
        await Assert.That(result.Fields[1].Value).IsEqualTo("Smith");
    }

    [Test]
    public async Task Parses_file_upload()
    {
        var fileContent = "Hello, World!";
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"hello.txt\"\r\n"
            + "Content-Type: text/plain\r\n"
            + "\r\n"
            + fileContent
            + "\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(0);
        await Assert.That(result.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("file");
        await Assert.That(result.Files[0].FileName).IsEqualTo("hello.txt");
        await Assert.That(result.Files[0].ContentType).IsEqualTo("text/plain");
        await Assert
            .That(Encoding.UTF8.GetString(result.Files[0].Content.Span))
            .IsEqualTo(fileContent);
        await Assert.That(result.Files[0].Content.Span.Overlaps(request.Body.Span)).IsTrue();
    }

    [Test]
    public async Task Parses_mixed_fields_and_files()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"title\"\r\n"
            + "\r\n"
            + "My Document\r\n"
            + "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"doc\"; filename=\"doc.pdf\"\r\n"
            + "Content-Type: application/pdf\r\n"
            + "\r\n"
            + "PDF-DATA\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("title");
        await Assert.That(result.Fields[0].Value).IsEqualTo("My Document");
        await Assert.That(result.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("doc");
        await Assert.That(result.Files[0].FileName).IsEqualTo("doc.pdf");
        await Assert.That(result.Files[0].ContentType).IsEqualTo("application/pdf");
        await Assert.That(Encoding.UTF8.GetString(result.Files[0].Content.Span)).IsEqualTo("PDF-DATA");
        await Assert.That(result.Files[0].Content.Span.Overlaps(request.Body.Span)).IsTrue();
    }

    [Test]
    public async Task File_content_can_contain_boundary_like_bytes_without_truncation()
    {
        var fileContent = "prefix--boundarysuffix";
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"data.txt\"\r\n"
            + "Content-Type: text/plain\r\n"
            + "\r\n"
            + fileContent
            + "\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(result.Files[0].Content.Span)).IsEqualTo(fileContent);
        await Assert.That(result.Files[0].Content.Span.Overlaps(request.Body.Span)).IsTrue();
    }

    [Test]
    public async Task File_content_can_contain_boundary_like_line_without_closing_or_crlf_match()
    {
        var fileContent = "prefix\r\n--boundaryX\r\nsuffix";
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"data.txt\"\r\n"
            + "Content-Type: text/plain\r\n"
            + "\r\n"
            + fileContent
            + "\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(result.Files[0].Content.Span)).IsEqualTo(fileContent);
    }

    [Test]
    public async Task Proper_closing_boundary_terminates_multipart_body()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"username\"\r\n"
            + "\r\n"
            + "alice\r\n"
            + "--boundary--\r\n"
            + "ignored trailer";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Value).IsEqualTo("alice");
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Returns_null_for_non_multipart_content_type()
    {
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/submit",
            Version = "HTTP/1.1",
            HeaderFields =  [new("Content-Type", "application/json"), new("Host", "localhost"),],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Host"] = "localhost",
            },
            Body = "{}"u8.ToArray(),
        };

        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_no_content_type_header()
    {
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/submit",
            Version = "HTTP/1.1",
            HeaderFields =  [],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Body = ReadOnlyMemory<byte>.Empty,
        };

        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task File_without_content_type_defaults_to_octet_stream()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"data.bin\"\r\n"
            + "\r\n"
            + "BINARY\r\n"
            + "--boundary--\r\n";

        var request = CreateMultipartRequest("boundary", body);
        var result = MultipartFormDataParser.Parse(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].ContentType).IsEqualTo("application/octet-stream");
    }

    [Test]
    public async Task Extracts_boundary_from_quoted_parameter()
    {
        var boundary = MultipartFormDataParser.ExtractBoundary(
            "multipart/form-data; boundary=\"my-boundary\""
        );

        await Assert.That(boundary).IsEqualTo("my-boundary");
    }

    [Test]
    public async Task Extracts_boundary_from_unquoted_parameter()
    {
        var boundary = MultipartFormDataParser.ExtractBoundary(
            "multipart/form-data; boundary=simple"
        );

        await Assert.That(boundary).IsEqualTo("simple");
    }
}
