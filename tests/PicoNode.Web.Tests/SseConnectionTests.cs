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

    [Test]
    public async Task WriteEventAsync_normalizes_lone_carriage_returns_and_blank_lines()
    {
        // SSE line splitting, spelled out: CRLF breaks a line, a lone CR is *deleted*
        // (not a break), and every line — including empty and trailing ones — gets its own
        // "data: " line. These are the cases the writer's scanner has to reproduce exactly.
        var cases = new (string Data, string Expected)[]
        {
            ("a\rb", "event: t\ndata: ab\n\n"),
            ("a\r\r\nb", "event: t\ndata: a\ndata: b\n\n"),
            ("\r", "event: t\ndata: \n\n"),
            ("\r\r\r", "event: t\ndata: \n\n"),
            ("a\r\n", "event: t\ndata: a\ndata: \n\n"),
            ("", "event: t\ndata: \n\n"),
            ("\n", "event: t\ndata: \ndata: \n\n"),
            ("\n\n\n", "event: t\ndata: \ndata: \ndata: \ndata: \n\n"),
            ("a\n\nb", "event: t\ndata: a\ndata: \ndata: b\n\n"),
            ("a\r\nb\r\nc", "event: t\ndata: a\ndata: b\ndata: c\n\n"),
        };

        foreach (var (data, expected) in cases)
        {
            var pipe = new Pipe();
            var sse = new SseConnection(pipe.Writer, TimeSpan.Zero);
            await sse.WriteEventAsync("t", data, CancellationToken.None);
            await pipe.Writer.CompleteAsync();

            var result = await pipe.Reader.ReadAsync(CancellationToken.None);
            await Assert
                .That(Encoding.UTF8.GetString(result.Buffer))
                .IsEqualTo(expected)
                .Because($"data {Escape(data)} must normalize to {Escape(expected)}");
            pipe.Reader.AdvanceTo(result.Buffer.End);
        }
    }

    private static string Escape(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n");

    [Test]
    public async Task Each_event_is_readable_before_the_next_write()
    {
        // Latency is part of the SSE contract: a write must return only once its event is
        // in the pipe, not batched with whatever comes next. A reader that had to wait for
        // a following write would make a live stream look like a dead one.
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.Zero);

        await sse.WriteJsonAsync("""{"seq":1}""", CancellationToken.None);

        // No second write, no completion: only the event itself can make this return.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var first = await pipe.Reader.ReadAsync(cts.Token);

        await Assert
            .That(Encoding.UTF8.GetString(first.Buffer))
            .IsEqualTo("data: {\"seq\":1}\n\n")
            .Because("the first event must be flushed on its own, not batched with the next");
        await Assert
            .That(first.IsCompleted)
            .IsFalse()
            .Because("the stream is still open — the event alone made this readable");
        pipe.Reader.AdvanceTo(first.Buffer.End);

        // The same for the next event: exactly one flush per event, nothing carried over.
        await sse.WriteJsonAsync("""{"seq":2}""", CancellationToken.None);
        var second = await pipe.Reader.ReadAsync(cts.Token);
        await Assert.That(Encoding.UTF8.GetString(second.Buffer)).IsEqualTo("data: {\"seq\":2}\n\n");
        pipe.Reader.AdvanceTo(second.Buffer.End);
    }

    // ── Allocation guards (audit F-26) ─────────────────────────────────
    //
    // One SSE event must cost no garbage on the writer side: it is formatted
    // straight into the pipe writer's own segment instead of being built as an
    // interpolated string plus a UTF-8 byte[] that is then copied in. The
    // ceiling is 16 B — below the 24-byte object header of any array or string a
    // re-introduced copy would have to allocate, because a percentage of a zero
    // measurement would be meaningless.
    private const double EventAllocationCeilingBytes = 16.0;
    private const int AllocationRounds = 5;
    private const int EventsPerRound = 100;

    private const string DeltaJson =
        """{"id":"chatcmpl-7f3a","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello from the model"}}]}""";

    [Test]
    public async Task Write_json_event_allocates_nothing_per_event()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.Zero);

        var (allocated, events, bytesWritten) = await MeasureEventsAsync(sse, pipe.Reader, DeltaJson);
        var perEvent = (double)allocated / events;

        // The measured region really wrote whole events: a no-op write would
        // otherwise look like a free one.
        await Assert
            .That(bytesWritten)
            .IsEqualTo(events * (6L + Encoding.UTF8.GetByteCount(DeltaJson) + 2L))
            .Because("the measured region must have written every byte of every event");
        await Assert
            .That(perEvent)
            .IsLessThan(EventAllocationCeilingBytes)
            .Because($"measured {perEvent:F1} B per event ({allocated} B over {events} events)");
    }

    [Test]
    public async Task Write_json_event_with_multibyte_text_allocates_nothing_per_event()
    {
        // UTF-8 sizing must be exact rather than "worst case per char": a text
        // payload is the one shape where a lazy fix falls back to GetBytes.
        const string json = """{"delta":"中文与笑脸 😀 éèê"}""";
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer, TimeSpan.Zero);

        var (allocated, events, bytesWritten) = await MeasureEventsAsync(sse, pipe.Reader, json);
        var perEvent = (double)allocated / events;

        await Assert
            .That(bytesWritten)
            .IsEqualTo(events * (6L + Encoding.UTF8.GetByteCount(json) + 2L))
            .Because("multi-byte text must be written as its exact UTF-8 length");
        await Assert
            .That(perEvent)
            .IsLessThan(EventAllocationCeilingBytes)
            .Because($"measured {perEvent:F1} B per event ({allocated} B over {events} events)");
    }

    /// <summary>
    /// 32 warm-up events outside the window (JIT, the UTF-8 encoding statics, the
    /// pipe's first segment), then 5 rounds x 100 events, each round counted with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. The drained bytes are
    /// summed so the caller can prove the region did real work.
    /// </summary>
    private static async Task<(long Allocated, long Events, long BytesWritten)> MeasureEventsAsync(
        SseConnection sse,
        PipeReader reader,
        string json
    )
    {
        for (var i = 0; i < 32; i++)
        {
            await WriteSynchronouslyAsync(sse, json);
            DrainAvailable(reader);
        }

        long allocated = 0;
        long events = 0;
        long bytesWritten = 0;
        for (var round = 0; round < AllocationRounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < EventsPerRound; i++)
            {
                await WriteSynchronouslyAsync(sse, json);
                bytesWritten += DrainAvailable(reader);
                events++;
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return (allocated, events, bytesWritten);
    }

    /// <summary>
    /// Writes one event and fails if it did not complete synchronously: a
    /// suspending write would continue on another thread and
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> would not see its
    /// allocations, so the measured number would be a lie.
    /// </summary>
    private static Task WriteSynchronouslyAsync(SseConnection sse, string json)
    {
        var task = sse.WriteJsonAsync(json, CancellationToken.None);
        if (!task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "the event write did not complete synchronously, so per-thread allocation counting is meaningless"
            );
        }

        return task;
    }

    private static long DrainAvailable(PipeReader reader)
    {
        long consumed = 0;
        while (reader.TryRead(out var result))
        {
            consumed += result.Buffer.Length;
            reader.AdvanceTo(result.Buffer.End);
        }

        return consumed;
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
