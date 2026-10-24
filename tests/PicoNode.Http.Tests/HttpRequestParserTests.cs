using System.Runtime.CompilerServices;

namespace PicoNode.Http.Tests;

public sealed class HttpRequestParserTests
{
    [Test]
    public async Task Parse_successfully_materializes_one_request_and_exact_consumed_position()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /submit HTTP/1.1\r\nHost: Example.com\r\nContent-Length: 5\r\n\r\nhe"
            ),
            Encoding.ASCII.GetBytes("lloNEXT")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Request).IsNotNull();
        var request =
            result.Request ?? throw new InvalidOperationException("Request should be present.");
        await Assert.That(request.Method).IsEqualTo("POST");
        await Assert.That(request.Target).IsEqualTo("/submit");
        await Assert.That(request.Version).IsEqualTo(HttpVersion.Http11);
        await Assert.That(request.Headers["host"]).IsEqualTo("Example.com");
        await Assert.That(request.Headers["content-length"]).IsEqualTo("5");
        await Assert.That(Encoding.ASCII.GetString(request.Body.ToArray())).IsEqualTo("hello");
        await Assert
            .That(Encoding.ASCII.GetString(buffer.Slice(result.Consumed).ToArray()))
            .IsEqualTo("NEXT");
    }

    [Test]
    public async Task Parse_preserves_repeated_request_headers_and_combines_lookup_values()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: example.com\r\nAccept: text/plain\r\naccept: text/html\r\nConnection: keep-alive\r\nConnection: close\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request).IsNotNull();

        var request =
            result.Request ?? throw new InvalidOperationException("Request should be present.");
        await Assert.That(request.HeaderFields.Count).IsEqualTo(5);
        await Assert
            .That(request.HeaderFields[0])
            .IsEqualTo(new KeyValuePair<string, string>("Host", "example.com"));
        await Assert
            .That(request.HeaderFields[1])
            .IsEqualTo(new KeyValuePair<string, string>("Accept", "text/plain"));
        await Assert
            .That(request.HeaderFields[2])
            .IsEqualTo(new KeyValuePair<string, string>("accept", "text/html"));
        await Assert.That(request.Headers["Accept"]).IsEqualTo("text/plain, text/html");
        await Assert.That(request.Headers["Connection"]).IsEqualTo("keep-alive, close");
    }

    [Test]
    public async Task Parse_request_line_spanning_buffer_segments_succeeds()
    {
        // Force the request line to span two buffer segments.
        // Seg1: "GET / HTT"      (9 bytes)
        // Seg2: "P/1.1\r\nHost: x\r\n\r\n"  (18 bytes)
        // The line "GET / HTTP/1.1\r" crosses the segment boundary.
        // FirstSpan.Slice would fail because FirstSpan only covers seg1.
        var buffer = CreateSequence(
            "GET / HTT"u8.ToArray(),
            "P/1.1\r\nHost: x\r\n\r\n"u8.ToArray()
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Request).IsNotNull();
        var request = result.Request!;
        await Assert.That(request.Method).IsEqualTo("GET");
        await Assert.That(request.Target).IsEqualTo("/");
        await Assert.That(request.Version).IsEqualTo(HttpVersion.Http11);
    }

    [Test]
    public async Task Parse_incomplete_body_consumes_nothing()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhe"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Incomplete);
        await Assert.That(result.Request).IsNull();
        await Assert.That(result.Error).IsNull();
        await Assert
            .That(Encoding.ASCII.GetString(buffer.Slice(result.Consumed).ToArray()))
            .IsEqualTo("POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhe");
    }

    [Test]
    public async Task Parse_rejects_unsupported_transfer_encoding()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /submit HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: gzip\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.UnsupportedFraming);
    }

    [Test]
    public async Task Parse_chunked_single_chunk()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request).IsNotNull();
        await Assert.That(result.Request!.Method).IsEqualTo("POST");
        await Assert.That(Encoding.ASCII.GetString(result.Request.Body.Span)).IsEqualTo("hello");
    }

    [Test]
    public async Task Parse_chunked_multiple_chunks()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert
            .That(Encoding.ASCII.GetString(result.Request!.Body.Span))
            .IsEqualTo("hello world");
    }

    [Test]
    public async Task Parse_chunked_empty_body()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Body.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_chunked_with_hex_sizes()
    {
        var chunk = new string('A', 16);
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                $"POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n{chunk}\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Body.Length).IsEqualTo(16);
    }

    [Test]
    public async Task Parse_chunked_with_chunk_extension()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=val\r\nhello\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(Encoding.ASCII.GetString(result.Request!.Body.Span)).IsEqualTo("hello");
    }

    [Test]
    public async Task Parse_chunked_incomplete_returns_incomplete()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhel"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Incomplete);
    }

    [Test]
    public async Task Parse_rejects_chunked_with_content_length()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
    }

    [Test]
    public async Task Parse_rejects_chunk_size_that_overflows_to_zero()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n100000000\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidChunkedBody);
    }

    [Test]
    public async Task Parse_rejects_chunk_size_that_overflows_to_small_positive_value()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n100000001\r\nA\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidChunkedBody);
    }

    [Test]
    public async Task Parse_accepts_duplicate_identical_content_length()
    {
        // RFC 7230 §3.3.2: multiple identical Content-Length values are legal;
        // only CONFLICTING values must be rejected.
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /submit HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\ncontent-length: 5\r\n\r\nhello"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request).IsNotNull();
        await Assert.That(result.Request!.Body.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_rejects_conflicting_content_length_values()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /submit HTTP/1.1\r\nContent-Length: 5\r\ncontent-length: 6\r\n\r\nhello!"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.DuplicateContentLength);
        await Assert.That(result.Request).IsNull();
    }

    [Test]
    public async Task Parse_rejects_transfer_encoding_with_content_length_as_framing_error()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /submit HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert
            .That(result.Error)
            .IsEqualTo(HttpRequestParseError.UnsupportedFraming)
            .Because("TE+CL together is a framing violation, not a request-line error");
    }

    [Test]
    public async Task Parse_rejects_malformed_request_line()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("POST /submit\r\nContent-Length: 5\r\n\r\nhello")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
        await Assert.That(result.Request).IsNull();
    }

    [Test]
    public async Task Parse_rejects_request_targets_without_a_leading_slash()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET foo HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_rejects_request_targets_with_fragments()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET /hello#frag HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_rejects_request_targets_with_invalid_percent_encoding()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET /%GG HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_accepts_request_targets_with_valid_query_strings_and_percent_encoding()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "GET /hello%20world?name=pico HTTP/1.1\r\nHost: example.com\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request).IsNotNull();
        await Assert.That(result.Request?.Target).IsEqualTo("/hello%20world?name=pico");
    }

    [Test]
    public async Task Parse_rejects_request_targets_with_raw_backslashes()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET /path\\segment HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_rejects_request_targets_with_raw_non_ascii_bytes()
    {
        var prefix = Encoding.ASCII.GetBytes("GET /");
        var suffix = Encoding.ASCII.GetBytes(" HTTP/1.1\r\nHost: example.com\r\n\r\n");
        var bytes = new byte[prefix.Length + 1 + suffix.Length];

        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0xFF;
        suffix.CopyTo(bytes, prefix.Length + 1);

        var buffer = CreateSequence(bytes);
        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_rejects_size_limit_violations()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET /very-long-path HTTP/1.1\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (_, _) => default,
                MaxRequestBytes = 8,
            }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.RequestTooLarge);
        await Assert.That(result.Request).IsNull();
    }

    [Test]
    public async Task Parse_accepts_body_larger_than_MaxRequestBytes_but_within_MaxRequestBodySize()
    {
        // Body of 16 KB — exceeds MaxRequestBytes (8 KB) but well within
        // MaxRequestBodySize (64 MB). Must not be rejected as RequestTooLarge.
        var bodySize = 16 * 1024;
        var body = new byte[bodySize];
        var header = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: {bodySize}\r\n\r\n"
        );

        var buffer = CreateSequence(header, body);

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request).IsNotNull();
    }

    [Test]
    public async Task Parse_rejects_body_exceeding_MaxRequestBodySize()
    {
        // MaxRequestBodySize = 100 bytes; body is 101 bytes.
        var bodySize = 101;
        var body = new byte[bodySize];
        var header = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: {bodySize}\r\n\r\n"
        );

        var buffer = CreateSequence(header, body);

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (_, _) => default,
                MaxRequestBodySize = 100,
            }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.RequestTooLarge);
    }

    [Test]
    public async Task Parse_reports_invalid_headers_when_header_line_missing_carriage_return()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHeader);
    }

    [Test]
    public async Task Parse_rejects_http11_requests_without_host_header()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nAccept: text/plain\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.MissingHostHeader);
    }

    [Test]
    public async Task Parse_rejects_header_names_with_non_token_characters()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: example.com\r\nBad/Header: value\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHeader);
    }

    [Test]
    public async Task Parse_rejects_header_values_with_control_characters()
    {
        var prefix = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nX-Test: ok");
        var suffix = Encoding.ASCII.GetBytes("bad\r\n\r\n");
        var bytes = new byte[prefix.Length + 1 + suffix.Length];

        prefix.CopyTo(bytes, 0);
        bytes[prefix.Length] = 0x01;
        suffix.CopyTo(bytes, prefix.Length + 1);

        var buffer = CreateSequence(bytes);

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHeader);
    }

    [Test]
    public async Task Parse_rejects_duplicate_host_headers()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\nHost: example.com\r\nHost: example.org\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_empty_host_header_values()
    {
        var buffer = CreateSequence(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost:   \r\n\r\n"));

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_with_commas()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com, example.org\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_accepts_host_header_values_with_ports()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com:8080\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
    }

    [Test]
    public async Task Parse_accepts_ipv4_host_header_values()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
    }

    [Test]
    public async Task Parse_accepts_bracketed_ipv6_host_header_values()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: [::1]:8080\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_with_schemes()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: http://example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_with_paths()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com/path\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_with_invalid_ports()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com:65536\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_without_brackets_for_ipv6_literals()
    {
        var buffer = CreateSequence(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: ::1\r\n\r\n"));

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_with_invalid_label_shapes()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: -example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_rejects_host_header_values_with_trailing_dots()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com.\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHostHeader);
    }

    [Test]
    public async Task Parse_accepts_http10_requests_without_host_header()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nAccept: text/plain\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Version).IsEqualTo(HttpVersion.Http10);
        await Assert.That(result.Request.Method).IsEqualTo("GET");
    }

    [Test]
    public async Task Parse_accepts_http10_requests_with_host_header()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /data HTTP/1.0\r\nHost: example.com\r\nContent-Length: 3\r\n\r\nabc"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Version).IsEqualTo(HttpVersion.Http10);
        await Assert.That(result.Request.Method).IsEqualTo("POST");
        await Assert.That(Encoding.ASCII.GetString(result.Request.Body.Span)).IsEqualTo("abc");
    }

    [Test]
    public async Task Parse_rejects_unsupported_http_versions()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/2.0\r\nHost: example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_sets_expects_continue_when_body_incomplete()
    {
        // Headers complete, body not yet arrived
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Incomplete);
        await Assert.That(result.ExpectsContinue).IsTrue();
    }

    [Test]
    public async Task Parse_does_not_set_expects_continue_when_body_complete()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\nhello"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Method).IsEqualTo("POST");
    }

    [Test]
    public async Task Parse_does_not_set_expects_continue_for_http10()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes(
                "POST /upload HTTP/1.0\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"
            )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Incomplete);
        await Assert.That(result.ExpectsContinue).IsFalse();
    }

    private static ReadOnlySequence<byte> CreateSequence(params ReadOnlyMemory<byte>[] segments)
    {
        if (segments.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var first = new BufferSegment(segments[0]);
        var last = first;

        for (var index = 1; index < segments.Length; index++)
        {
            last = last.Append(segments[index]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length };

            Next = segment;
            return segment;
        }
    }

    [Test]
    public async Task Chunked_body_larger_than_max_request_bytes_is_accepted()
    {
        // Default MaxRequestBytes = 8192; body of 16 KB must pass (bounded by MaxRequestBodySize).
        var chunkData = new string('x', 16 * 1024);
        var raw = Encoding.ASCII.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n"
                + chunkData.Length.ToString("x")
                + "\r\n"
                + chunkData
                + "\r\n0\r\n\r\n"
        );
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
        };

        var result = HttpRequestParser.Parse(new ReadOnlySequence<byte>(raw), options);

        await Assert
            .That(result.Status)
            .IsEqualTo(HttpRequestParseStatus.Success)
            .Because(
                "chunked body size must be governed by MaxRequestBodySize, not MaxRequestBytes"
            );
    }

    [Test]
    public async Task Chunked_body_over_max_request_body_size_is_rejected()
    {
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            MaxRequestBodySize = 1024,
        };
        var chunkData = new string('y', 2048);
        var raw = Encoding.ASCII.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n"
                + chunkData.Length.ToString("x")
                + "\r\n"
                + chunkData
                + "\r\n0\r\n\r\n"
        );

        var result = HttpRequestParser.Parse(new ReadOnlySequence<byte>(raw), options);

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
    }

    /// <summary>
    /// Header shape of a plain keep-alive API request: five header lines, every name a well-known
    /// HTTP name written in the canonical casing real clients send.
    /// </summary>
    private const string ApiRequest =
        "GET /api/items HTTP/1.1\r\n"
        + "Host: example.com\r\n"
        + "User-Agent: piconode-loadtest/1.0\r\n"
        + "Accept: */*\r\n"
        + "Accept-Encoding: gzip, deflate\r\n"
        + "Connection: keep-alive\r\n"
        + "\r\n";

    /// <summary>
    /// The same request plus two headers whose names the parser cannot know, and values long enough
    /// that the value strings — which the parser must always materialize — dominate.
    /// </summary>
    private const string BrowserRequest =
        "GET /assets/app.js HTTP/1.1\r\n"
        + "Host: example.com\r\n"
        + "User-Agent: Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36\r\n"
        + "Accept: */*\r\n"
        + "Accept-Encoding: gzip, deflate, br\r\n"
        + "Accept-Language: en-US,en;q=0.9\r\n"
        + "Connection: keep-alive\r\n"
        + "Cookie: session=8f14e45fceea167a5a36dedd4bea2543; theme=dark\r\n"
        + "\r\n";

    [Test]
    public async Task Parse_keeps_wire_order_and_repeats_when_the_header_index_grows()
    {
        // 24 header lines over 22 distinct names: past the initial index capacity and past the first
        // growth, so the index is rebuilt while a repeated name is already anchored in it.
        var lines = new List<string> { "Host: example.com", "Accept: text/plain" };
        for (var index = 0; index < 19; index++)
        {
            lines.Add($"X-Trace-{index}: v{index}");
        }

        lines.Add("accept: text/html");
        lines.Add("Cookie: a=1");
        lines.Add("Cookie: b=2");

        var raw = Encoding.ASCII.GetBytes(
            "GET /many HTTP/1.1\r\n" + string.Join("\r\n", lines) + "\r\n\r\n"
        );

        var result = HttpRequestParser.Parse(
            new ReadOnlySequence<byte>(raw),
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        var request =
            result.Request ?? throw new InvalidOperationException("Request should be present.");

        // HeaderFields: every line, in arrival order, spelled as the client wrote it.
        await Assert.That(request.HeaderFields.Count).IsEqualTo(24);
        await Assert
            .That(request.HeaderFields[0])
            .IsEqualTo(new KeyValuePair<string, string>("Host", "example.com"));
        await Assert
            .That(request.HeaderFields[1])
            .IsEqualTo(new KeyValuePair<string, string>("Accept", "text/plain"));
        // Grown index still reports this line by its own spelling, not the first line's.
        await Assert
            .That(request.HeaderFields[21])
            .IsEqualTo(new KeyValuePair<string, string>("accept", "text/html"));
        await Assert
            .That(request.HeaderFields[23])
            .IsEqualTo(new KeyValuePair<string, string>("Cookie", "b=2"));

        // Headers: one entry per distinct name, values joined in arrival order.
        await Assert.That(request.Headers.Count).IsEqualTo(22);
        await Assert.That(request.Headers["ACCEPT"]).IsEqualTo("text/plain, text/html");
        await Assert.That(request.Headers["Cookie"]).IsEqualTo("a=1, b=2");
        await Assert.That(request.Headers["x-trace-18"]).IsEqualTo("v18");
        await Assert.That(request.Headers["X-Trace-0"]).IsEqualTo("v0");
        await Assert.That(request.Headers.ContainsKey("missing")).IsFalse();

        // Enumerating the dictionary view yields distinct names — 22 entries, not 24 lines.
        var distinct = request.Headers.ToArray();
        await Assert.That(distinct.Length).IsEqualTo(22);
        await Assert.That(distinct.Count(entry => entry.Key == "Accept")).IsEqualTo(1);
    }

    /// <summary>
    /// Ceiling for one parsed <see cref="ApiRequest"/> — the whole request, not just its headers.
    /// Measured 1 688 B per request with the old two-collection shape (a 16-slot <c>List</c> of fields
    /// plus a 16-slot <c>Dictionary</c> index, and two strings per header) and 864 B per request after
    /// it became one collection with interned names; see
    /// <c>.superpowers/sdd/2026-10-22-hot-paths/task-T1.7-report.md</c> for the method and the spread.
    /// </summary>
    /// <remarks>
    /// 1 024 B is ~18 % above the measured value: enough that a different runtime's string and object
    /// headers do not trip it, tight enough that it still fails by a wide margin if the parsing goes
    /// back to two collections (+~500 B) or to a string per header name (+~240 B here).
    /// </remarks>
    private const long ApiRequestAllocationCeiling = 1_024;

    /// <summary>
    /// Ceiling for one parsed <see cref="BrowserRequest"/>: eight header lines, two of them with names
    /// the parser has to materialize, and long values. Measured 2 048 B per request before and 1 184 B
    /// after; the ceiling keeps ~18 % of headroom over the measurement and stays well under the old
    /// shape.
    /// </summary>
    private const long BrowserRequestAllocationCeiling = 1_400;

    /// <summary>
    /// Ceiling for the marginal cost of one extra header line. Interning the well-known names leaves
    /// only the value string to allocate (48 B measured); a name string per header pushed this to
    /// 94 B, so anything above this ceiling means the names are being copied again.
    /// </summary>
    private const long PerHeaderAllocationCeiling = 64;

    [Test]
    public async Task Parse_allocates_one_header_collection_per_request()
    {
        var api = MeasureAllocationPerRequest(ApiRequest);
        var browser = MeasureAllocationPerRequest(BrowserRequest);
        Console.WriteLine(
            $"http1-header-allocation api={api.BytesPerRequest:F0}B browser={browser.BytesPerRequest:F0}B "
                + $"ceilings={ApiRequestAllocationCeiling}B/{BrowserRequestAllocationCeiling}B"
        );

        // The measurement is only a measurement if it parsed headers: a parsing regression that
        // returned no request would otherwise show up as a suspiciously cheap request.
        await Assert.That(api.ParsedHeaders).IsGreaterThan(0);
        await Assert.That(browser.ParsedHeaders).IsGreaterThan(0);
        await Assert
            .That(api.BytesPerRequest)
            .IsLessThan(ApiRequestAllocationCeiling)
            .Because(
                $"one request must not pay for two header collections and two strings per header "
                    + $"(measured {api.BytesPerRequest:F0} B per request)"
            );
        await Assert
            .That(browser.BytesPerRequest)
            .IsLessThan(BrowserRequestAllocationCeiling)
            .Because(
                $"the same, for a request with names the parser cannot know "
                    + $"(measured {browser.BytesPerRequest:F0} B per request)"
            );
    }

    [Test]
    public async Task Parse_allocates_the_value_only_for_a_wellknown_header_name()
    {
        var oneHeader = MeasureAllocationPerRequest(
            "GET /api/items HTTP/1.1\r\nHost: example.com\r\n\r\n"
        );
        // Four more lines, all well-known names in canonical casing, all values the length of the
        // one above — so the only variable left is the name the parser has to materialize.
        var fiveHeaders = MeasureAllocationPerRequest(
            "GET /api/items HTTP/1.1\r\n"
                + "Host: example.com\r\n"
                + "User-Agent: example.com\r\n"
                + "Accept: example.com\r\n"
                + "Connection: example.com\r\n"
                + "Authorization: example.com\r\n"
                + "\r\n"
        );

        var perHeader = (fiveHeaders.BytesPerRequest - oneHeader.BytesPerRequest) / 4.0;
        Console.WriteLine(
            $"http1-header-allocation oneHeader={oneHeader.BytesPerRequest:F0}B "
                + $"fiveHeaders={fiveHeaders.BytesPerRequest:F0}B perHeader={perHeader:F1}B "
                + $"ceiling={PerHeaderAllocationCeiling}B"
        );

        await Assert.That(fiveHeaders.ParsedHeaders).IsGreaterThan(oneHeader.ParsedHeaders);
        await Assert
            .That(perHeader)
            .IsLessThan(PerHeaderAllocationCeiling)
            .Because(
                $"a well-known header name must borrow the shared constant instead of allocating "
                    + $"its own string (measured {perHeader:F1} B per header line)"
            );
    }

    /// <summary>
    /// Bytes allocated per parse of <paramref name="raw"/> and the header count the parses reported
    /// (so a caller can prove the parses actually happened), measured on the calling thread over
    /// <see cref="AllocationRounds"/> rounds of <see cref="AllocationIterations"/> parses. Rounds are
    /// summed, not sampled: a stray GC or JIT event in one round cannot then drag the number around.
    /// </summary>
    private static (double BytesPerRequest, int ParsedHeaders) MeasureAllocationPerRequest(
        string raw
    )
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var buffer = new ReadOnlySequence<byte>(bytes);
        var options = new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default };

        // Outside the measured window: JIT, encoding statics, and whatever the first parse touches.
        for (var index = 0; index < 32; index++)
        {
            Consume(HttpRequestParser.Parse(buffer, options));
        }

        var total = 0L;
        var parsedHeaders = 0;
        for (var round = 0; round < AllocationRounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < AllocationIterations; index++)
            {
                parsedHeaders += Consume(HttpRequestParser.Parse(buffer, options));
            }

            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return (total / (double)(AllocationRounds * AllocationIterations), parsedHeaders);
    }

    private const int AllocationRounds = 5;
    private const int AllocationIterations = 100;

    /// <summary>
    /// Reads the parsed request, so the parse cannot be optimised away: its return value is summed
    /// into the measurement's <c>ParsedHeaders</c>, which every caller asserts on.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Consume(HttpRequestParseResult result) =>
        result.Request is { } request
            ? request.Headers.Count + request.HeaderFields.Count
            : -1;
}
