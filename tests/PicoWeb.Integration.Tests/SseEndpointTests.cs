namespace PicoWeb.Integration.Tests;

/// <summary>
/// Integration tests for Server-Sent Events (SSE).
/// </summary>
public sealed class SseEndpointTests
{
    // ── SseConnection formatting ───────────────────────────────────────

    [Test]
    public async Task WriteEventAsync_formats_correctly()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("status", "Starting demo...", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var readResult = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        // Should contain "event: status\ndata: Starting demo...\n\n"
        await Assert.That(text).Contains("event: status");
        await Assert.That(text).Contains("data: Starting demo...");
        await Assert.That(text).EndsWith("\n\n");
    }

    [Test]
    public async Task WriteEventAsync_splits_multiline_data()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("text", "line1\nline2", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var readResult = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        await Assert.That(text).Contains("event: text");
        await Assert.That(text).Contains("data: line1");
        await Assert.That(text).Contains("data: line2");
    }

    [Test]
    public async Task WriteErrorAsync_formats_json_correctly()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteErrorAsync("something went wrong", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var readResult = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        await Assert.That(text).Contains("event: error");
        await Assert.That(text).Contains("data: ");
        // The data should be JSON with the escaped message
        await Assert.That(text).Contains("something went wrong");
    }

    [Test]
    public async Task CompleteAsync_sends_done_signal()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.CompleteAsync(CancellationToken.None);

        var readResult = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        await Assert.That(text).Contains("data: [DONE]");
        await Assert.That(readResult.IsCompleted).IsTrue();
    }

    [Test]
    public async Task PingAsync_sends_keepalive_comment()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.PingAsync(CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var readResult = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        await Assert.That(text).Contains(": keepalive");
    }

    // ── Input validation ───────────────────────────────────────────────

    [Test]
    public async Task WriteEventAsync_rejects_empty_event_type()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sse.WriteEventAsync("", "data", CancellationToken.None)
        );
    }

    [Test]
    public async Task WriteEventAsync_rejects_event_type_with_newline()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sse.WriteEventAsync("bad\ntype", "data", CancellationToken.None)
        );
    }

    [Test]
    public async Task WriteEventAsync_handles_null_data()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("empty", null!, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var readResult = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);

        // Should have "data: \n" (empty data line)
        await Assert.That(text).Contains("data: ");
    }

    // ── SseEndpoint factory ────────────────────────────────────────────

    [Test]
    public async Task SseEndpoint_creates_handler_with_correct_headers()
    {
        var handler = SseEndpoint.Create(
            static async (sse, ct) =>
            {
                await sse.WriteEventAsync("done", """{"ok":true}""", ct);
                await sse.CompleteAsync(ct);
            }
        );

        var context = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/sse/demo" }
        );
        var response = await handler(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers["Content-Type"]).IsEqualTo("text/event-stream");
        await Assert.That(response.Headers["Cache-Control"]).IsEqualTo("no-cache");
        await Assert.That(response.BodyStream).IsNotNull();
    }

    [Test]
    public async Task SseEndpoint_stream_contains_event_data()
    {
        var handler = SseEndpoint.Create(
            static async (sse, ct) =>
            {
                await sse.WriteEventAsync("status", "hello", ct);
                await sse.CompleteAsync(ct);
            }
        );

        var context = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/sse/demo" }
        );
        var response = await handler(context, CancellationToken.None);

        await Assert.That(response.BodyStream).IsNotNull();
        using var reader = new StreamReader(response.BodyStream!);
        var body = await reader.ReadToEndAsync();

        await Assert.That(body).Contains("event: status");
        await Assert.That(body).Contains("data: hello");
        await Assert.That(body).Contains("data: [DONE]");
    }

    [Test]
    public async Task SseEndpoint_stream_closes_on_writer_error()
    {
        var handler = SseEndpoint.Create(
            static async (sse, ct) =>
            {
                await sse.WriteEventAsync("start", "begin", ct);
                throw new InvalidOperationException("test failure");
            }
        );

        var context = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/sse/demo" }
        );
        var response = await handler(context, CancellationToken.None);

        await Assert.That(response.BodyStream).IsNotNull();
        // Writer error completes the pipe — reader may see the exception.
        try
        {
            using var reader = new StreamReader(response.BodyStream!);
            var body = await reader.ReadToEndAsync();
            await Assert.That(body).Contains("event: start");
            await Assert.That(body).DoesNotContain("[DONE]");
        }
        catch (InvalidOperationException)
        {
            // Acceptable: writer error propagates through pipe.
        }
    }
}
