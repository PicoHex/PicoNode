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
        // Deterministic trigger: the ping is caused by advancing the clock.
        var time = new ManualTimeProvider();
        var interval = TimeSpan.FromSeconds(10);
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, interval) { TimeProvider = time };

        // One frame starts the lazy loop; then the connection goes silent.
        await sse.WriteAsync("data: hello\n\n", CancellationToken.None);

        time.Advance(interval + TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var output = await ReadUntilKeepAliveAsync(pipe.Reader, cts.Token);

        await Assert
            .That(output)
            .Contains(": keepalive")
            .Because("idle SSE connection must emit keep-alive comment frames");

        await sse.CompleteAsync(CancellationToken.None);
        await sse.StopKeepAliveAsync();
    }

    [Test]
    public async Task Writes_within_each_interval_suppress_keepalive_pings()
    {
        // Deterministic replacement for the real-time busy-stream test: the old
        // version depended on Task.Delay(5) actually keeping up with a 200ms
        // interval, so thread-pool starvation produced false failures. Idle is a
        // function of the injected clock — interleaving writes with clock advances
        // proves "idle-only pinging" without any real-time assumption.
        var time = new ManualTimeProvider();
        var interval = TimeSpan.FromSeconds(10);
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, interval) { TimeProvider = time };

        // First write starts the loop; its timer is due one interval from now.
        await sse.WriteAsync("data: first\n\n", CancellationToken.None);
        time.Advance(interval / 2);
        await sse.WriteAsync("data: second\n\n", CancellationToken.None);
        // The loop wakes here and must see half an interval of idle, not a full one.
        time.Advance(interval / 2);
        await sse.WriteAsync("data: third\n\n", CancellationToken.None);
        await sse.CompleteAsync(CancellationToken.None);
        await sse.StopKeepAliveAsync();

        var output = await ReadAllTextAsync(pipe.Reader);

        await Assert
            .That(output.Contains(": keepalive"))
            .IsFalse()
            .Because("every keep-alive wake must find the stream written to within the interval");
        await Assert.That(output).Contains("data: first");
        await Assert.That(output).Contains("data: second");
        await Assert.That(output).Contains("data: third");
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
        var time = new ManualTimeProvider();
        var interval = TimeSpan.FromSeconds(10);
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, interval) { TimeProvider = time };

        await sse.WriteAsync("data: first\n\n", CancellationToken.None);

        // First automatic ping, triggered deterministically.
        time.Advance(interval + TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await ReadUntilKeepAliveAsync(pipe.Reader, cts.Token);

        await sse.StopKeepAliveAsync();

        // With the loop stopped, further clock advances must not produce pings.
        time.Advance(interval * 2);
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
