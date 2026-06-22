namespace PicoNode.Web.Tests;

public sealed class SseConnectionTests
{
    [Test]
    public async Task WriteEventAsync_emits_event_and_data_lines()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("text_delta", "hello", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: text_delta\ndata: hello\n\n");
    }

    [Test]
    public async Task WriteEventAsync_splits_multiline_data()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("code", "line1\nline2", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: code\ndata: line1\ndata: line2\n\n");
    }

    [Test]
    public async Task WriteEventAsync_normalizes_crlf()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("text", "a\r\nb", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: text\ndata: a\ndata: b\n\n");
    }

    [Test]
    public async Task WriteEventAsync_null_data_emits_empty_data_line()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("ping", null!, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: ping\ndata: \n\n");
    }

    [Test]
    public async Task WriteEventAsync_empty_data_emits_empty_data_line()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteEventAsync("ping", "", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: ping\ndata: \n\n");
    }

    [Test]
    public async Task WriteEventAsync_throws_on_null_event_type()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert
            .That(async () => await sse.WriteEventAsync(null!, "data", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WriteEventAsync_throws_on_empty_event_type()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert
            .That(async () => await sse.WriteEventAsync("", "data", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WriteEventAsync_throws_on_event_type_with_newline()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert
            .That(async () => await sse.WriteEventAsync("a\nb", "data", CancellationToken.None))
            .Throws<ArgumentException>();
    }
}
