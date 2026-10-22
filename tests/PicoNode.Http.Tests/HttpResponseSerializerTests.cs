namespace PicoNode.Http.Tests;

public sealed class HttpResponseSerializerTests
{
    [Test]
    public async Task Serialize_writes_status_headers_and_body_as_sendable_sequence()
    {
        var body = System.Text.Encoding.ASCII.GetBytes("pong");
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers =
            [
                new KeyValuePair<string, string>("Content-Type", "text/plain"),
                new KeyValuePair<string, string>("Content-Length", "999"),
                new KeyValuePair<string, string>("connection", "keep-alive"),
            ],
            Body = body,
        };

        var serialized = HttpResponseSerializer.Serialize(response);
        var segments = GetSegments(serialized);

        await Assert.That(segments.Length).IsEqualTo(2);
        await Assert
            .That(GetAsciiString(segments[0]))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/plain\r\n"
                    + "connection: keep-alive\r\n"
                    + "Content-Length: 4\r\n"
                    + "\r\n"
            );
        await Assert.That(GetAsciiString(segments[1])).IsEqualTo("pong");
        await Assert
            .That(GetAsciiString(serialized.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/plain\r\n"
                    + "connection: keep-alive\r\n"
                    + "Content-Length: 4\r\n"
                    + "\r\n"
                    + "pong"
            );
    }

    [Test]
    public async Task Serialize_preserves_connection_upgrade_header_in_101_switching_protocols_response()
    {
        var response = new HttpResponse
        {
            StatusCode = 101,
            ReasonPhrase = "Switching Protocols",
            Headers =
            [
                new KeyValuePair<string, string>("Upgrade", "websocket"),
                new KeyValuePair<string, string>("Connection", "Upgrade"),
                new KeyValuePair<string, string>(
                    "Sec-WebSocket-Accept",
                    "s3pPLMBiTxaQ9kYGzzhZRbK+xOo="
                ),
            ],
        };

        var serialized = HttpResponseSerializer.Serialize(response);

        var text = GetAsciiString(serialized.ToArray());
        await Assert.That(text).Contains("Connection: Upgrade");
    }

    [Test]
    public async Task Serialize_adds_server_and_connection_close_headers_from_serializer_inputs()
    {
        var response = new HttpResponse
        {
            StatusCode = 204,
            ReasonPhrase = "No Content",
            Headers =
            [
                new KeyValuePair<string, string>("X-Trace-Id", "abc123"),
                new KeyValuePair<string, string>("Server", "application"),
            ],
        };

        var serialized = HttpResponseSerializer.Serialize(
            response,
            closeConnection: true,
            serverHeader: "PicoNode"
        );

        await Assert.That(serialized.IsSingleSegment).IsTrue();
        await Assert
            .That(GetAsciiString(serialized.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 204 No Content\r\n"
                    + "X-Trace-Id: abc123\r\n"
                    + "Server: PicoNode\r\n"
                    + "Connection: close\r\n"
                    + "\r\n"
            );
    }

    [Test]
    public async Task Serialize_preserves_application_server_header_when_no_serializer_server_header_is_configured()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("Server", "application")],
        };

        var serialized = HttpResponseSerializer.Serialize(response);

        await Assert
            .That(GetAsciiString(serialized.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n" + "Server: application\r\n" + "Content-Length: 0\r\n" + "\r\n"
            );
    }

    [Test]
    public async Task Serialize_rejects_reason_phrases_with_line_breaks()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK\r\nInjected: value",
        };

        await Assert
            .That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_invalid_header_names()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("Bad:Header", "value")],
        };

        await Assert
            .That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_non_token_header_names()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("Bad/Header", "value")],
        };

        await Assert
            .That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_header_values_with_line_breaks()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("X-Test", "value\r\nInjected: true")],
        };

        await Assert
            .That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_invalid_server_header_input()
    {
        var response = new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" };

        await Assert
            .That(() =>
                HttpResponseSerializer.Serialize(
                    response,
                    serverHeader: "PicoNode\r\nInjected: true"
                )
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_header_values_with_control_characters()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("X-Test", $"value{(char)1}")],
        };

        await Assert
            .That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_server_header_values_with_control_characters()
    {
        var response = new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" };

        await Assert
            .That(() =>
                HttpResponseSerializer.Serialize(response, serverHeader: $"PicoNode{(char)1}")
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SerializeChunkedHeaders_emits_transfer_encoding_and_no_content_length()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers =
            [
                new KeyValuePair<string, string>("X-Custom", "value"),
                new KeyValuePair<string, string>("Content-Length", "999"),
            ],
        };

        var serialized = HttpResponseSerializer.SerializeChunkedHeaders(response);

        var text = GetAsciiString(serialized.ToArray());
        await Assert.That(text).Contains("Transfer-Encoding: chunked");
        await Assert.That(text).DoesNotContain("Content-Length");
        await Assert.That(text).Contains("X-Custom: value");
        await Assert.That(text).EndsWith("\r\n\r\n");
    }

    [Test]
    public async Task FormatChunk_formats_data_with_hex_size_and_crlf_delimiters()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("Hello");
        var chunk = HttpResponseSerializer.FormatChunk(data);

        await Assert.That(GetAsciiString(chunk.ToArray())).IsEqualTo("5\r\nHello\r\n");
    }

    [Test]
    public async Task ChunkTerminator_returns_final_chunk()
    {
        var terminator = HttpResponseSerializer.ChunkTerminator;

        await Assert.That(GetAsciiString(terminator.ToArray())).IsEqualTo("0\r\n\r\n");
    }

    private static ReadOnlyMemory<byte>[] GetSegments(ReadOnlySequence<byte> sequence)
    {
        var segments = new List<ReadOnlyMemory<byte>>();

        foreach (var segment in sequence)
        {
            segments.Add(segment);
        }

        return [.. segments];
    }

    private static string GetAsciiString(ReadOnlyMemory<byte> buffer) =>
        System.Text.Encoding.ASCII.GetString(buffer.Span);

    private static string GetAsciiString(byte[] buffer) =>
        System.Text.Encoding.ASCII.GetString(buffer);

    [Test]
    public async Task Serialize_204_has_no_content_length()
    {
        var response = new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" };

        var text = GetAsciiString(HttpResponseSerializer.Serialize(response).ToArray());

        await Assert
            .That(text)
            .DoesNotContain("Content-Length")
            .Because("RFC 9110 §8.6: a 204 response MUST NOT carry Content-Length");
        await Assert.That(text).IsEqualTo("HTTP/1.1 204 No Content\r\n\r\n");
    }

    [Test]
    public async Task Serialize_101_has_no_content_length()
    {
        // The real WebSocket handshake response, not a hand-built 101: RFC 6455
        // upgrades and RFC 9110 §8.6 (no Content-Length on any 1xx) meet here.
        var headerFields = new List<KeyValuePair<string, string>>
        {
            new("Host", "localhost"),
            new("Upgrade", "websocket"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ=="),
            new("Sec-WebSocket-Version", "13"),
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/ws",
            Version = HttpVersion.Http11,
            HeaderFields = headerFields,
            Headers = headerFields.ToDictionary(
                h => h.Key,
                h => h.Value,
                StringComparer.OrdinalIgnoreCase
            ),
            Body = ReadOnlyMemory<byte>.Empty,
        };

        var response = WebSocketUpgrade.TryUpgrade(request);
        await Assert.That(response).IsNotNull();

        var text = GetAsciiString(HttpResponseSerializer.Serialize(response!).ToArray());

        await Assert.That(text).StartsWith("HTTP/1.1 101 Switching Protocols\r\n");
        await Assert.That(text).Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
        await Assert
            .That(text)
            .DoesNotContain("Content-Length")
            .Because("RFC 9110 §8.6: a 1xx response MUST NOT carry Content-Length");
    }

    [Test]
    public async Task Serialize_drops_content_length_when_transfer_encoding_present()
    {
        // RFC 9112 §6.1/§6.3: Transfer-Encoding and Content-Length must never
        // disagree about framing on one message. The serializer owns framing, so
        // the application's Transfer-Encoding is dropped and the buffered body is
        // announced with the Content-Length the serializer itself computes.
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("Transfer-Encoding", "chunked")],
            Body = "hello"u8.ToArray(),
        };

        var text = GetAsciiString(HttpResponseSerializer.Serialize(response).ToArray());

        await Assert
            .That(text)
            .DoesNotContain("Transfer-Encoding")
            .Because("the serializer owns framing; an application Transfer-Encoding would"
                + " tell a proxy to read the raw body as chunk framing (response smuggling)"
            );
        await Assert.That(text).Contains("Content-Length: 5");
        await Assert.That(text).EndsWith("\r\n\r\nhello");
    }

    [Test]
    public async Task Serialize_304_echoes_application_content_length_only()
    {
        var withLength = new HttpResponse
        {
            StatusCode = 304,
            ReasonPhrase = "Not Modified",
            Headers = [new KeyValuePair<string, string>("Content-Length", "1234")],
        };
        var synthesizedOnly = new HttpResponse
        {
            StatusCode = 304,
            ReasonPhrase = "Not Modified",
            Body = "hello"u8.ToArray(),
        };

        var echoed = GetAsciiString(HttpResponseSerializer.Serialize(withLength).ToArray());
        var none = GetAsciiString(HttpResponseSerializer.Serialize(synthesizedOnly).ToArray());

        await Assert
            .That(echoed)
            .Contains("Content-Length: 1234")
            .Because("RFC 9110 §15.4.5: a 304 may repeat the length a 200 would have carried");
        await Assert
            .That(none)
            .DoesNotContain("Content-Length")
            .Because("a synthesized Content-Length on 304 is a length the 200 never had");
    }

    [Test]
    public async Task SerializeChunkedHeaders_ignores_application_transfer_encoding()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [new KeyValuePair<string, string>("Transfer-Encoding", "gzip, chunked")],
        };

        var text = GetAsciiString(
            HttpResponseSerializer.SerializeChunkedHeaders(response).ToArray()
        );

        await Assert.That(text).Contains("Transfer-Encoding: chunked\r\n");
        await Assert.That(text).DoesNotContain("gzip");
        await Assert.That(text).DoesNotContain("Content-Length");
    }

    [Test]
    public async Task Serialize_does_not_send_a_body_for_statuses_that_have_no_content()
    {
        foreach (var (statusCode, reasonPhrase) in new[]
        {
            (101, "Switching Protocols"),
            (204, "No Content"),
            (304, "Not Modified"),
        })
        {
            var response = new HttpResponse
            {
                StatusCode = statusCode,
                ReasonPhrase = reasonPhrase,
                Body = "leaked"u8.ToArray(),
            };

            var text = GetAsciiString(HttpResponseSerializer.Serialize(response).ToArray());

            await Assert
                .That(text)
                .EndsWith("\r\n\r\n")
                .Because("RFC 9110 §6.4.1: a " + statusCode + " response has no content");
            await Assert
                .That(text)
                .DoesNotContain("leaked")
                .Because(
                    "an unframed body on a "
                        + statusCode
                        + " response desyncs the next response on a keep-alive connection"
                );
        }
    }

    [Test]
    public async Task Non_ascii_header_values_are_not_mangled()
    {
        var response = new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers =
            [
                new KeyValuePair<string, string>(
                    "Content-Disposition",
                    "attachment; filename=\"报告.pdf\""
                ),
            ],
            Body = ReadOnlyMemory<byte>.Empty,
        };

        var wire = HttpResponseSerializer.Serialize(response).ToArray();
        var text = Encoding.UTF8.GetString(wire);

        await Assert
            .That(text)
            .Contains("报告.pdf")
            .Because("UTF-8 header values must reach the wire unchanged");
    }
}
