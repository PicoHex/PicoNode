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
            Version = HttpVersion.Http11,
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
        var result = await MultipartFormDataParser.ParseAsync(request);

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
        var result = await MultipartFormDataParser.ParseAsync(request);

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
        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(0);
        await Assert.That(result.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("file");
        await Assert.That(result.Files[0].FileName).IsEqualTo("hello.txt");
        await Assert.That(result.Files[0].ContentType).IsEqualTo("text/plain");
        await Assert
            .That(Encoding.UTF8.GetString(await ReadFileAsync(result.Files[0])))
            .IsEqualTo(fileContent);
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
        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("title");
        await Assert.That(result.Fields[0].Value).IsEqualTo("My Document");
        await Assert.That(result.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("doc");
        await Assert.That(result.Files[0].FileName).IsEqualTo("doc.pdf");
        await Assert.That(result.Files[0].ContentType).IsEqualTo("application/pdf");
        await Assert
            .That(Encoding.UTF8.GetString(await ReadFileAsync(result.Files[0])))
            .IsEqualTo("PDF-DATA");
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
        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert
            .That(Encoding.UTF8.GetString(await ReadFileAsync(result.Files[0])))
            .IsEqualTo(fileContent);
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
        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert
            .That(Encoding.UTF8.GetString(await ReadFileAsync(result.Files[0])))
            .IsEqualTo(fileContent);
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
        var result = await MultipartFormDataParser.ParseAsync(request);

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
            Version = HttpVersion.Http11,
            HeaderFields = [new("Content-Type", "application/json"), new("Host", "localhost")],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Host"] = "localhost",
            },
            Body = "{}"u8.ToArray(),
        };

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Returns_null_when_no_content_type_header()
    {
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/submit",
            Version = HttpVersion.Http11,
            HeaderFields = [],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Body = ReadOnlyMemory<byte>.Empty,
        };

        var result = await MultipartFormDataParser.ParseAsync(request);

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
        var result = await MultipartFormDataParser.ParseAsync(request);

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

    [Test]
    public async Task Returns_null_for_empty_boundary_parameter()
    {
        var boundary = MultipartFormDataParser.ExtractBoundary("multipart/form-data; boundary=");

        await Assert.That(boundary).IsNull();
    }

    [Test]
    public async Task Returns_null_for_boundary_with_invalid_separator_characters()
    {
        var boundary = MultipartFormDataParser.ExtractBoundary(
            "multipart/form-data; boundary=bad(boundary)"
        );

        await Assert.That(boundary).IsNull();
    }

    [Test]
    public async Task Returns_null_when_boundary_exceeds_configured_max_length()
    {
        var boundary = MultipartFormDataParser.ExtractBoundary(
            "multipart/form-data; boundary=abcdef",
            maxBoundaryLength: 5
        );

        await Assert.That(boundary).IsNull();
    }

    [Test]
    public async Task Parse_rejects_invalid_boundary_length_option_values()
    {
        var request = CreateMultipartRequest(
            "boundary",
            Encoding.ASCII.GetBytes("--boundary--\r\n")
        );

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(
                    request,
                    new MultipartFormDataParserOptions { MaxBoundaryLength = 0 }
                )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(
                    request,
                    new MultipartFormDataParserOptions
                    {
                        MaxBoundaryLength =
                            MultipartFormDataParserOptions.DefaultMaxBoundaryLength + 1,
                    }
                )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Parse_honors_configured_boundary_length_limit()
    {
        var body =
            "--boundary\r\nContent-Disposition: form-data; name=\"username\"\r\n\r\nalice\r\n--boundary--\r\n";
        var request = CreateMultipartRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(
            request,
            new MultipartFormDataParserOptions { MaxBoundaryLength = 5 }
        );

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Skips_text_field_with_invalid_utf8_bytes()
    {
        var boundary = "boundary"u8.ToArray();
        var headerPrefix = Encoding.ASCII.GetBytes(
            "--boundary\r\nContent-Disposition: form-data; name=\"username\"\r\n\r\n"
        );
        var footer = Encoding.ASCII.GetBytes("\r\n--boundary--\r\n");
        var bodyBytes = new byte[headerPrefix.Length + 2 + footer.Length];

        headerPrefix.CopyTo(bodyBytes, 0);
        bodyBytes[headerPrefix.Length] = 0xC3;
        bodyBytes[headerPrefix.Length + 1] = 0x28;
        footer.CopyTo(bodyBytes, headerPrefix.Length + 2);

        var request = CreateMultipartRequest(
            new string(boundary.Select(static b => (char)b).ToArray()),
            bodyBytes
        );
        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(0);
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Skips_part_with_invalid_utf8_headers()
    {
        var headerPrefix = Encoding.ASCII.GetBytes(
            "--boundary\r\nContent-Disposition: form-data; name=\""
        );
        var headerSuffix = Encoding.ASCII.GetBytes("\"\r\n\r\nalice\r\n--boundary--\r\n");
        var bodyBytes = new byte[headerPrefix.Length + 1 + headerSuffix.Length];

        headerPrefix.CopyTo(bodyBytes, 0);
        bodyBytes[headerPrefix.Length] = 0xFF;
        headerSuffix.CopyTo(bodyBytes, headerPrefix.Length + 1);

        var request = CreateMultipartRequest("boundary", bodyBytes);
        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(0);
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    private static HttpRequest CreateMultipartRequest(string boundary, byte[] bodyBytes)
    {
        return new HttpRequest
        {
            Method = "POST",
            Target = "/upload",
            Version = HttpVersion.Http11,
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
            BodyStream = new MemoryStream(bodyBytes, writable: false),
        };
    }

    [Test]
    public async Task Parses_multipart_from_body_stream()
    {
        var body =
            "--bound\r\n"
            + "Content-Disposition: form-data; name=\"field1\"\r\n"
            + "\r\n"
            + "value1\r\n"
            + "--bound--\r\n";

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/upload",
            Version = HttpVersion.Http11,
            HeaderFields =
            [
                new("Content-Type", "multipart/form-data; boundary=bound"),
                new("Content-Length", bodyBytes.Length.ToString()),
                new("Host", "localhost"),
            ],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "multipart/form-data; boundary=bound",
                ["Content-Length"] = bodyBytes.Length.ToString(),
                ["Host"] = "localhost",
            },
            BodyStream = new MemoryStream(bodyBytes, writable: false),
        };

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("field1");
        await Assert.That(result.Fields[0].Value).IsEqualTo("value1");
    }

    [Test]
    public async Task InMemory_part_larger_than_MaxPartSizeBytes_throws()
    {
        // Regression: ParseBody (the request.Body path) ignored
        // MaxPartSizeBytes/MaxTotalSizeBytes, while the streaming path enforced
        // them — the same request succeeded or failed depending on which body
        // representation it used.
        var body =
            "--b\r\n"
            + "Content-Disposition: form-data; name=\"f\"; filename=\"a.bin\"\r\n"
            + "Content-Type: application/octet-stream\r\n"
            + "\r\n"
            + new string('x', 100)
            + "\r\n--b--\r\n";
        var request = CreateMultipartRequest("b", body);
        var options = new MultipartFormDataParserOptions { MaxPartSizeBytes = 10 };

        await Assert
            .That(async () => await MultipartFormDataParser.ParseAsync(request, options))
            .Throws<InvalidDataException>()
            .Because(
                "the in-memory path must enforce the same part-size limit as the streaming path"
            );
    }

    [Test]
    public async Task InMemory_total_larger_than_MaxTotalSizeBytes_throws()
    {
        var body =
            "--b\r\n"
            + "Content-Disposition: form-data; name=\"a\"\r\n"
            + "\r\n"
            + new string('x', 20)
            + "\r\n--b\r\n"
            + "Content-Disposition: form-data; name=\"b\"\r\n"
            + "\r\n"
            + new string('y', 20)
            + "\r\n--b--\r\n";
        var request = CreateMultipartRequest("b", body);
        var options = new MultipartFormDataParserOptions { MaxTotalSizeBytes = 20 };

        await Assert
            .That(async () => await MultipartFormDataParser.ParseAsync(request, options))
            .Throws<InvalidDataException>()
            .Because(
                "the in-memory path must enforce the same total-size limit as the streaming path"
            );
    }

    [Test]
    public async Task InMemory_part_and_total_exactly_at_limit_are_accepted()
    {
        var body =
            "--b\r\n"
            + "Content-Disposition: form-data; name=\"a\"\r\n"
            + "\r\n"
            + new string('x', 10)
            + "\r\n--b--\r\n";
        var request = CreateMultipartRequest("b", body);
        // Headers are part of the budget, so "exactly at the limit" is header bytes + 10.
        var headerBytes = Encoding.UTF8.GetByteCount(
            "Content-Disposition: form-data; name=\"a\"\r\n"
        );
        var options = new MultipartFormDataParserOptions
        {
            MaxPartSizeBytes = 10,
            MaxTotalSizeBytes = headerBytes + 10,
        };

        var result = await MultipartFormDataParser.ParseAsync(request, options);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Value).IsEqualTo(new string('x', 10));
    }

    // ── parameter parsing: `name=` must not match inside `filename=` ──────────

    [Test]
    public async Task Name_parameter_is_not_matched_inside_filename()
    {
        // Audit F-45: an unanchored IndexOf("name=") matches the tail of
        // "filename=", so the declared name is discarded and the attacker's file
        // name is promoted to the field name.
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; filename=\"evil.txt\"; name=\"role\"\r\n"
            + "\r\n"
            + "admin\r\n"
            + "--boundary--\r\n";
        var request = CreateMultipartRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("role");
        await Assert.That(result.Files[0].FileName).IsEqualTo("evil.txt");
    }

    [Test]
    public async Task Name_parameter_is_not_matched_inside_filename_on_the_streaming_path()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; filename=\"evil.txt\"; name=\"role\"\r\n"
            + "\r\n"
            + "admin\r\n"
            + "--boundary--\r\n";
        var request = CreateStreamingRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("role");
        await Assert.That(result.Files[0].FileName).IsEqualTo("evil.txt");
    }

    [Test]
    public async Task Part_without_a_name_parameter_is_skipped()
    {
        // With the anchored parser the part declares no name at all, so it is not a
        // form value: it must be dropped rather than filed under "evil.txt".
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; filename=\"evil.txt\"\r\n"
            + "\r\n"
            + "admin\r\n"
            + "--boundary--\r\n";
        var request = CreateMultipartRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(0);
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Part_without_a_name_parameter_is_skipped_on_the_streaming_path()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; filename=\"evil.txt\"\r\n"
            + "\r\n"
            + "admin\r\n"
            + "--boundary--\r\n";
        var request = CreateStreamingRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(0);
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Semicolon_inside_a_quoted_filename_does_not_split_the_parameter()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=\"doc\"; filename=\"a;b.txt\"\r\n"
            + "Content-Type: text/plain\r\n"
            + "\r\n"
            + "data\r\n"
            + "--boundary--\r\n";
        var request = CreateMultipartRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("doc");
        await Assert.That(result.Files[0].FileName).IsEqualTo("a;b.txt");
    }

    [Test]
    public async Task Parameter_name_is_matched_case_insensitively_and_with_surrounding_spaces()
    {
        var body =
            "--boundary\r\n"
            + "Content-Disposition: form-data; NAME = \"doc\"; FILENAME=\"a.txt\"\r\n"
            + "\r\n"
            + "data\r\n"
            + "--boundary--\r\n";
        var request = CreateMultipartRequest("boundary", body);

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("doc");
        await Assert.That(result.Files[0].FileName).IsEqualTo("a.txt");
    }

    // ── part count ──────────────────────────────────────────────────────────

    [Test]
    public async Task Part_count_is_bounded()
    {
        // 300 empty parts: every one of them costs no content bytes, so only a part
        // count can bound the number of allocations a 64 MB body can buy.
        var request = CreateMultipartRequest("b", BuildParts("b", partCount: 300, content: ""));

        await Assert
            .That(async () => await MultipartFormDataParser.ParseAsync(request))
            .Throws<InvalidDataException>()
            .Because("an unbounded part count is a heap amplifier, not a payload limit");
    }

    [Test]
    public async Task Part_count_is_bounded_on_the_streaming_path()
    {
        var request = CreateStreamingRequest("b", BuildParts("b", partCount: 300, content: "x"));

        await Assert
            .That(async () => await MultipartFormDataParser.ParseAsync(request))
            .Throws<InvalidDataException>()
            .Because("both parse paths must bound the part count");
    }

    [Test]
    public async Task Part_count_at_the_default_limit_is_accepted()
    {
        var request = CreateMultipartRequest("b", BuildParts("b", partCount: 256, content: ""));

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(256);
    }

    [Test]
    public async Task Part_count_limit_is_configurable()
    {
        var two = BuildParts("b", partCount: 2, content: "");
        var three = BuildParts("b", partCount: 3, content: "");
        var options = new MultipartFormDataParserOptions { MaxPartCount = 2 };

        var accepted = await MultipartFormDataParser.ParseAsync(
            CreateMultipartRequest("b", two),
            options
        );
        await Assert.That(accepted).IsNotNull();
        await Assert.That(accepted!.Fields.Count).IsEqualTo(2);

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(CreateMultipartRequest("b", three), options)
            )
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Parse_rejects_non_positive_part_limits()
    {
        var request = CreateMultipartRequest("b", BuildParts("b", partCount: 1, content: "x"));

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(
                    request,
                    new MultipartFormDataParserOptions { MaxPartCount = 0 }
                )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(
                    request,
                    new MultipartFormDataParserOptions { MaxHeaderBytesPerPart = 0 }
                )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    // ── header bytes ────────────────────────────────────────────────────────

    [Test]
    public async Task Part_header_bytes_are_bounded()
    {
        var request = CreateMultipartRequest("b", BuildPaddedBody("b", padLength: 9000));

        await Assert
            .That(async () => await MultipartFormDataParser.ParseAsync(request))
            .Throws<InvalidDataException>()
            .Because("unbounded part headers are parsed into strings the size limit never counted");
    }

    [Test]
    public async Task Part_header_bytes_are_bounded_on_the_streaming_path()
    {
        var request = CreateStreamingRequest("b", BuildPaddedBody("b", padLength: 9000));

        await Assert
            .That(async () => await MultipartFormDataParser.ParseAsync(request))
            .Throws<InvalidDataException>()
            .Because("both parse paths must bound the per-part header size");
    }

    [Test]
    public async Task Header_bytes_count_toward_MaxTotalSizeBytes_on_both_paths()
    {
        // Two parts, 2 content bytes in total, ~1.7 KB of headers: a total limit of
        // 1 KB only trips when headers are part of the budget.
        var body = BuildPaddedBody("b", padLength: 800);
        var options = new MultipartFormDataParserOptions { MaxTotalSizeBytes = 1024 };

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(CreateMultipartRequest("b", body), options)
            )
            .Throws<InvalidDataException>()
            .Because("headers are attacker-supplied bytes and must be counted");

        await Assert
            .That(async () =>
                await MultipartFormDataParser.ParseAsync(CreateStreamingRequest("b", body), options)
            )
            .Throws<InvalidDataException>()
            .Because("headers are attacker-supplied bytes and must be counted");
    }

    [Test]
    public async Task Header_bytes_within_the_total_are_accepted()
    {
        // Positive control for the test above: the same body parses when the total
        // limit is large enough for headers + content.
        var body = BuildPaddedBody("b", padLength: 800);
        var options = new MultipartFormDataParserOptions { MaxTotalSizeBytes = 4096 };

        var result = await MultipartFormDataParser.ParseAsync(
            CreateMultipartRequest("b", body),
            options
        );

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Part_headers_exactly_at_the_limit_are_accepted_on_both_paths()
    {
        // The two paths have to charge the same bytes for a header block, otherwise one of
        // them refuses a body the other accepts.
        var headerBlock =
            "Content-Disposition: form-data; name=\"a\"\r\nX-Pad: " + new string('p', 16) + "\r\n";
        var body = "--b\r\n" + headerBlock + "\r\nv\r\n--b--\r\n";
        var options = new MultipartFormDataParserOptions
        {
            MaxHeaderBytesPerPart = headerBlock.Length,
        };

        var inMemory = await MultipartFormDataParser.ParseAsync(
            CreateMultipartRequest("b", body),
            options
        );
        await Assert.That(inMemory).IsNotNull();
        await Assert.That(inMemory!.Fields.Count).IsEqualTo(1);
        await Assert.That(inMemory.Fields[0].Value).IsEqualTo("v");

        var streaming = await MultipartFormDataParser.ParseAsync(
            CreateStreamingRequest("b", body),
            options
        );
        await Assert.That(streaming).IsNotNull();
        await Assert.That(streaming!.Fields.Count).IsEqualTo(1);
        await Assert.That(streaming.Fields[0].Value).IsEqualTo("v");
    }

    private static string BuildParts(string boundary, int partCount, string content)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < partCount; i++)
        {
            builder
                .Append("--")
                .Append(boundary)
                .Append("\r\nContent-Disposition: form-data; name=\"f")
                .Append(i)
                .Append("\"\r\n\r\n")
                .Append(content)
                .Append("\r\n");
        }

        return builder.Append("--").Append(boundary).Append("--\r\n").ToString();
    }

    private static string BuildPaddedBody(string boundary, int padLength)
    {
        var pad = new string('p', padLength);
        var builder = new StringBuilder();
        foreach (var name in new[] { "a", "b" })
        {
            builder
                .Append("--")
                .Append(boundary)
                .Append("\r\nContent-Disposition: form-data; name=\"")
                .Append(name)
                .Append("\"\r\nX-Pad: ")
                .Append(pad)
                .Append("\r\n\r\nx\r\n");
        }

        return builder.Append("--").Append(boundary).Append("--\r\n").ToString();
    }

    private static HttpRequest CreateStreamingRequest(string boundary, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return new HttpRequest
        {
            Method = "POST",
            Target = "/upload",
            Version = HttpVersion.Http11,
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
            BodyStream = new MemoryStream(bodyBytes, writable: false),
        };
    }

    internal static async Task<byte[]> ReadFileAsync(MultipartFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
