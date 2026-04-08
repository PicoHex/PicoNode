using PicoNode.Http.Internal;

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
        await Assert.That(GetAsciiString(segments[0]))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/plain\r\n"
                    + "Content-Length: 4\r\n"
                    + "\r\n"
            );
        await Assert.That(GetAsciiString(segments[1])).IsEqualTo("pong");
        await Assert.That(GetAsciiString(serialized.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/plain\r\n"
                    + "Content-Length: 4\r\n"
                    + "\r\n"
                    + "pong"
            );
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
        await Assert.That(GetAsciiString(serialized.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 204 No Content\r\n"
                    + "X-Trace-Id: abc123\r\n"
                    + "Server: PicoNode\r\n"
                    + "Connection: close\r\n"
                    + "Content-Length: 0\r\n"
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

        await Assert.That(GetAsciiString(serialized.ToArray()))
            .IsEqualTo(
                "HTTP/1.1 200 OK\r\n"
                    + "Server: application\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
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

        await Assert.That(() => HttpResponseSerializer.Serialize(response))
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

        await Assert.That(() => HttpResponseSerializer.Serialize(response))
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

        await Assert.That(() => HttpResponseSerializer.Serialize(response))
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

        await Assert.That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_invalid_server_header_input()
    {
        var response = new HttpResponse
        {
            StatusCode = 204,
            ReasonPhrase = "No Content",
        };

        await Assert.That(
            () => HttpResponseSerializer.Serialize(response, serverHeader: "PicoNode\r\nInjected: true")
        ).Throws<ArgumentException>();
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

        await Assert.That(() => HttpResponseSerializer.Serialize(response))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Serialize_rejects_server_header_values_with_control_characters()
    {
        var response = new HttpResponse
        {
            StatusCode = 204,
            ReasonPhrase = "No Content",
        };

        await Assert.That(
            () => HttpResponseSerializer.Serialize(response, serverHeader: $"PicoNode{(char)1}")
        ).Throws<ArgumentException>();
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
}
