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
    public async Task WriteErrorAsync_emits_error_event_with_json()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteErrorAsync("timeout", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: error\ndata: {\"message\":\"timeout\"}\n\n");
    }

    [Test]
    public async Task WriteErrorAsync_escapes_quotes_in_message()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteErrorAsync("unknown model \"gpt-5\"", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert
            .That(output)
            .IsEqualTo("event: error\ndata: {\"message\":\"unknown model \\\"gpt-5\\\"\"}\n\n");
    }

    [Test]
    public async Task WriteErrorAsync_replaces_newlines_with_spaces()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteErrorAsync("a\nb\rc", CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var reader = pipe.Reader;
        var result = await reader.ReadAsync(CancellationToken.None);
        var output = Encoding.UTF8.GetString(result.Buffer);

        await Assert.That(output).IsEqualTo("event: error\ndata: {\"message\":\"a b c\"}\n\n");
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

    [Test]
    public async Task Constructor_applies_keep_alive_interval()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.FromSeconds(3));

        await Assert.That(sse.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Default_keep_alive_interval_is_15_seconds()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await Assert.That(sse.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task Idle_connection_emits_keepalive_frames()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.FromMilliseconds(50));

        // One frame starts the lazy loop; then the connection goes silent.
        await sse.WriteAsync("data: hello\n\n", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var output = await ReadUntilKeepAliveAsync(pipe.Reader, cts.Token);

        await Assert
            .That(output)
            .Contains(": keepalive")
            .Because("idle SSE connection must emit keep-alive comment frames");

        await sse.CompleteAsync(CancellationToken.None);
        // Loop self-terminates on its next wake (failed write on completed pipe).
    }

    [Test]
    public async Task Busy_stream_does_not_get_interleaved_pings()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 600)
        {
            await sse.WriteAsync(
                $"data: {stopwatch.ElapsedMilliseconds}\n\n",
                CancellationToken.None
            );
            await Task.Delay(5);
        }
        await sse.CompleteAsync(CancellationToken.None);

        var output = await ReadAllTextAsync(pipe.Reader);
        await Assert
            .That(output.Contains(": keepalive"))
            .IsFalse()
            .Because("writes every 5ms keep the stream busy; idle-only pinging must stay silent");
    }

    [Test]
    public async Task Zero_keep_alive_interval_disables_pings()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.Zero);

        await sse.WriteAsync("data: hi\n\n", CancellationToken.None);
        await Task.Delay(150);
        await sse.CompleteAsync(CancellationToken.None);

        var output = await ReadAllTextAsync(pipe.Reader);
        await Assert.That(output.Contains(": keepalive")).IsFalse();
    }

    [Test]
    public async Task Concurrent_handler_writes_and_pings_do_not_corrupt_stream()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.FromMilliseconds(50));

        // Jittered 30-80ms gaps mean roughly half the gaps exceed the 50ms interval,
        // so pings really interleave with handler writes.
        var rng = new Random(42);
        for (var i = 0; i < 15; i++)
        {
            await sse.WriteAsync($"data: frame {i}\n\n", CancellationToken.None);
            await Task.Delay(rng.Next(30, 80));
        }
        await sse.CompleteAsync(CancellationToken.None);

        var output = await ReadAllTextAsync(pipe.Reader);
        for (var i = 0; i < 15; i++)
        {
            await Assert
                .That(output)
                .Contains($"data: frame {i}\n\n")
                .Because("interleaved write+flush pairs would corrupt frame bytes");
        }
    }

    private static async Task<string> ReadAllTextAsync(PipeReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var result = await reader.ReadAsync(CancellationToken.None);
            sb.Append(Encoding.UTF8.GetString(result.Buffer));
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return sb.ToString();
            }
        }
    }

    private static async Task<string> ReadUntilKeepAliveAsync(
        PipeReader reader,
        CancellationToken ct
    )
    {
        var sb = new StringBuilder();
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            sb.Append(Encoding.UTF8.GetString(result.Buffer));
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || sb.ToString().Contains(": keepalive"))
            {
                return sb.ToString();
            }
        }
    }

    [Test]
    public async Task StopKeepAliveAsync_stops_further_pings()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.FromMilliseconds(200));

        await sse.WriteAsync("data: first\n\n", CancellationToken.None);

        // Wait for the first automatic ping.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await ReadUntilKeepAliveAsync(pipe.Reader, cts.Token);

        await sse.StopKeepAliveAsync();

        // With the loop stopped, no further pings may appear.
        await Task.Delay(350);
        await sse.CompleteAsync(CancellationToken.None);
        var output = await ReadAllTextAsync(pipe.Reader);

        await Assert
            .That(CountOccurrences(output, ": keepalive"))
            .IsEqualTo(0)
            .Because("no further pings may appear after StopKeepAliveAsync");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
