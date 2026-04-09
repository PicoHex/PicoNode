using System.Text;
using PicoNode.Http.Internal;

namespace PicoNode.Http.Tests;

public sealed class HttpRequestParserTests
{
    [Test]
    public async Task Parse_successfully_materializes_one_request_and_exact_consumed_position()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /submit HTTP/1.1\r\nHost: Example.com\r\nContent-Length: 5\r\n\r\nhe"
                ),
            Encoding.ASCII.GetBytes("lloNEXT")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Request).IsNotNull();
        var request =
            result.Request ?? throw new InvalidOperationException("Request should be present.");
        await Assert.That(request.Method).IsEqualTo("POST");
        await Assert.That(request.Target).IsEqualTo("/submit");
        await Assert.That(request.Version).IsEqualTo("HTTP/1.1");
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
            Encoding
                .ASCII
                .GetBytes(
                    "GET / HTTP/1.1\r\nHost: example.com\r\nAccept: text/plain\r\naccept: text/html\r\nConnection: keep-alive\r\nConnection: close\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
    public async Task Parse_incomplete_body_consumes_nothing()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /submit HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhe"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            Encoding
                .ASCII
                .GetBytes(
                    "POST /submit HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: gzip\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.UnsupportedFraming);
    }

    [Test]
    public async Task Parse_chunked_single_chunk()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Body.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_chunked_with_hex_sizes()
    {
        var chunk = new string('A', 16);
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    $"POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n{chunk}\r\n0\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Body.Length).IsEqualTo(16);
    }

    [Test]
    public async Task Parse_chunked_with_chunk_extension()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=val\r\nhello\r\n0\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(Encoding.ASCII.GetString(result.Request!.Body.Span)).IsEqualTo("hello");
    }

    [Test]
    public async Task Parse_chunked_incomplete_returns_incomplete()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhel"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Incomplete);
    }

    [Test]
    public async Task Parse_rejects_chunked_with_content_length()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
    }

    [Test]
    public async Task Parse_rejects_duplicate_content_length()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /submit HTTP/1.1\r\nContent-Length: 5\r\ncontent-length: 5\r\n\r\nhello"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.DuplicateContentLength);
        await Assert.That(result.Request).IsNull();
    }

    [Test]
    public async Task Parse_rejects_malformed_request_line()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("POST /submit\r\nContent-Length: 5\r\n\r\nhello")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_accepts_request_targets_with_valid_query_strings_and_percent_encoding()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes("GET /hello%20world?name=pico HTTP/1.1\r\nHost: example.com\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request).IsNotNull();
        await Assert.That(result.Request?.Target).IsEqualTo("/hello%20world?name=pico");
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
    public async Task Parse_reports_invalid_headers_when_header_line_missing_carriage_return()
    {
        var buffer = CreateSequence(
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.MissingHostHeader);
    }

    [Test]
    public async Task Parse_rejects_header_names_with_non_token_characters()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nBad/Header: value\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidHeader);
    }

    [Test]
    public async Task Parse_rejects_duplicate_host_headers()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nHost: example.org\r\n\r\n")
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Version).IsEqualTo("HTTP/1.0");
        await Assert.That(result.Request.Method).IsEqualTo("GET");
    }

    [Test]
    public async Task Parse_accepts_http10_requests_with_host_header()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /data HTTP/1.0\r\nHost: example.com\r\nContent-Length: 3\r\n\r\nabc"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Version).IsEqualTo("HTTP/1.0");
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
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Rejected);
        await Assert.That(result.Error).IsEqualTo(HttpRequestParseError.InvalidRequestLine);
    }

    [Test]
    public async Task Parse_sets_expects_continue_when_body_incomplete()
    {
        // Headers complete, body not yet arrived
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Incomplete);
        await Assert.That(result.ExpectsContinue).IsTrue();
    }

    [Test]
    public async Task Parse_does_not_set_expects_continue_when_body_complete()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /upload HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\nhello"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
        );

        await Assert.That(result.Status).IsEqualTo(HttpRequestParseStatus.Success);
        await Assert.That(result.Request!.Method).IsEqualTo("POST");
    }

    [Test]
    public async Task Parse_does_not_set_expects_continue_for_http10()
    {
        var buffer = CreateSequence(
            Encoding
                .ASCII
                .GetBytes(
                    "POST /upload HTTP/1.0\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"
                )
        );

        var result = HttpRequestParser.Parse(
            buffer,
            new HttpConnectionHandlerOptions { RequestHandler = static (_, _) => default, }
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
            var segment = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };

            Next = segment;
            return segment;
        }
    }
}
