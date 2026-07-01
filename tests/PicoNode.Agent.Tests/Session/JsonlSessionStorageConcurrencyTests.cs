namespace PicoNode.Agent.Tests.Session;

/// <summary>
/// TDD Batch 7: JsonlSessionStorage concurrency.
///
/// Gap: JsonlSessionStorage.AppendEntry mutates _entries, _byId, _labelsById
/// and calls File.AppendAllTextAsync without any lock. Concurrent AppendEntry
/// calls can:
///   - interleave two writes so the on-disk file has partial/mixed lines,
///   - drop entries from _entries / _byId due to unsynchronised List/Dict mutation,
///   - throw InvalidOperationException from concurrent readers (GetEntries etc.).
///
/// This suite pins the contract: N concurrent AppendEntry calls must produce
/// exactly N well-formed lines on disk AND N entries in memory.
/// </summary>
public class JsonlSessionStorageConcurrencyTests
{
    [Test]
    public async Task AppendEntry_Concurrent_AllEntriesPersistedAndReadable()
    {
        var path = Path.Combine(Path.GetTempPath(), "pico-sess-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using var storage = await JsonlSessionStorage.CreateAsync(path, "sess-1");

            const int n = 100;
            var tasks = new Task[n];
            for (int i = 0; i < n; i++)
            {
                var idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    var id = await storage.CreateEntryId();
                    var entry = new MessageEntry
                    {
                        Id = id,
                        ParentId = null,
                        Timestamp = DateTime.UtcNow.ToString("O"),
                        Message = new Message { Role = "user", Content = $"msg-{idx}" },
                    };
                    await storage.AppendEntry(entry);
                });
            }
            await Task.WhenAll(tasks);

            // In-memory view must have all N entries.
            var entries = await storage.GetEntries();
            await Assert.That(entries.Length).IsEqualTo(n);

            // On-disk file must have exactly n+1 non-empty lines
            // (1 header line + N append lines) and each append line must parse.
            var lines = (await File.ReadAllLinesAsync(path))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            await Assert.That(lines.Length).IsEqualTo(n + 1);

            for (int i = 1; i < lines.Length; i++)
            {
                var parsed = SessionEntrySerializer.Deserialize(lines[i]);
                await Assert.That(parsed).IsNotNull();
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task GetEntries_DuringAppend_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "pico-sess-" + Guid.NewGuid() + ".jsonl");
        try
        {
            await using var storage = await JsonlSessionStorage.CreateAsync(path, "sess-2");

            const int n = 200;
            using var cts = new CancellationTokenSource();

            var writer = Task.Run(async () =>
            {
                for (int i = 0; i < n && !cts.IsCancellationRequested; i++)
                {
                    var id = await storage.CreateEntryId();
                    await storage.AppendEntry(
                        new MessageEntry
                        {
                            Id = id,
                            Timestamp = DateTime.UtcNow.ToString("O"),
                            Message = new Message { Role = "user", Content = "x" },
                        }
                    );
                }
            });

            // While the writer is going, spin the reader — it must never throw
            // InvalidOperationException from concurrent collection mutation.
            Exception? readerError = null;
            var reader = Task.Run(() =>
            {
                try
                {
                    while (!writer.IsCompleted)
                    {
                        _ = storage.GetEntries().Result;
                    }
                }
                catch (Exception ex)
                {
                    readerError = ex;
                }
            });

            await writer;
            cts.Cancel();
            await reader;

            await Assert.That(readerError).IsNull();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
