namespace PicoNode.Web.Tests;

public sealed class InMemorySessionStoreTests
{
    private static SessionOptions DefaultOptions =>
        new() { IdleTimeout = TimeSpan.FromMinutes(20), CleanupInterval = TimeSpan.FromMinutes(5) };

    [Test]
    public async Task CreateAsync_returns_new_session_with_id()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var session = await store.CreateAsync();

        await Assert.That(session.Id).IsNotNull();
        await Assert.That(session.Id).IsNotEmpty();
        await Assert.That(session.IsNew).IsTrue();
    }

    [Test]
    public async Task CreateAsync_sessions_have_unique_ids()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var s1 = await store.CreateAsync();
        var s2 = await store.CreateAsync();

        await Assert.That(s1.Id).IsNotEqualTo(s2.Id);
    }

    [Test]
    public async Task CreateAsync_does_not_retain_session_until_saved()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();

        // An unsaved session must not occupy store memory: SessionMiddleware
        // creates one for every cookie-less request and only persists it when
        // the handler actually dirties it (DoS guard).
        await Assert.That(await store.LoadAsync(created.Id)).IsNull();

        created.SetString("key", "value");
        await store.SaveAsync(created.Id, created);

        var loaded = await store.LoadAsync(created.Id);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GetString("key")).IsEqualTo("value");
    }

    [Test]
    public async Task LoadAsync_returns_null_for_unknown_id()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var session = await store.LoadAsync("nonexistent");

        await Assert.That(session).IsNull();
    }

    [Test]
    public async Task LoadAsync_returns_saved_session()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        await store.SaveAsync(created.Id, created);
        var loaded = await store.LoadAsync(created.Id);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Id).IsEqualTo(created.Id);
    }

    [Test]
    public async Task LoadAsync_loaded_session_IsNew_is_false()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        await store.SaveAsync(created.Id, created);
        var loaded = await store.LoadAsync(created.Id);

        await Assert.That(loaded!.IsNew).IsFalse();
    }

    [Test]
    public async Task SaveAsync_persists_data()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        created.SetString("key", "value");
        await store.SaveAsync(created.Id, created);

        var loaded = await store.LoadAsync(created.Id);
        await Assert.That(loaded!.GetString("key")).IsEqualTo("value");
    }

    [Test]
    public async Task DeleteAsync_removes_session()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        await store.DeleteAsync(created.Id);

        var loaded = await store.LoadAsync(created.Id);
        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task LoadAsync_twice_returns_same_session_object()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        await store.SaveAsync(created.Id, created);
        var loaded1 = await store.LoadAsync(created.Id);
        var loaded2 = await store.LoadAsync(created.Id);

        await Assert.That(ReferenceEquals(loaded1, loaded2)).IsTrue();
    }

    [Test]
    public async Task CreateAsync_after_dispose_throws()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.CreateAsync());
    }

    [Test]
    public async Task LoadAsync_after_dispose_throws()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.LoadAsync("any"));
    }

    [Test]
    public async Task Constructor_throws_on_zero_IdleTimeout()
    {
        await Assert
            .That(() =>
                new InMemorySessionStore(new SessionOptions { IdleTimeout = TimeSpan.Zero })
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_throws_on_zero_CleanupInterval()
    {
        await Assert
            .That(() =>
                new InMemorySessionStore(new SessionOptions { CleanupInterval = TimeSpan.Zero })
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Create_beyond_max_sessions_evicts_oldest()
    {
        using var store = new InMemorySessionStore(new SessionOptions { MaxSessions = 3 });

        var saved = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var created = await store.CreateAsync();
            created.SetString("n", i.ToString(CultureInfo.InvariantCulture));
            await store.SaveAsync(created.Id, created);
            saved.Add(created.Id);
        }

        var loadable = 0;
        foreach (var id in saved)
        {
            if (await store.LoadAsync(id) is not null)
            {
                loadable++;
            }
        }

        await Assert
            .That(loadable)
            .IsLessThanOrEqualTo(3)
            .Because(
                "an unauthenticated caller dirties a session per request; without a cap the "
                    + "store grows without bound"
            );
        await Assert
            .That(await store.LoadAsync(saved[0]))
            .IsNull()
            .Because("the least recently used session is the one that goes first");
        await Assert.That(await store.LoadAsync(saved[1])).IsNull();
        await Assert.That(await store.LoadAsync(saved[^1])).IsNotNull();
    }

    [Test]
    public async Task Create_beyond_max_sessions_evicts_nothing_until_saved()
    {
        // The cap is enforced where the dictionary actually grows (SaveAsync). Evicting on
        // CreateAsync would let any cookie-less request discard a live session without ever
        // persisting anything of its own.
        using var store = new InMemorySessionStore(new SessionOptions { MaxSessions = 2 });

        var kept = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var created = await store.CreateAsync();
            created.SetString("n", i.ToString(CultureInfo.InvariantCulture));
            await store.SaveAsync(created.Id, created);
            kept.Add(created.Id);
        }

        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync();
        }

        foreach (var id in kept)
        {
            await Assert.That(await store.LoadAsync(id)).IsNotNull();
        }
    }

    [Test]
    public async Task SetValue_beyond_cap_throws()
    {
        using var store = new InMemorySessionStore(
            new SessionOptions { MaxBytesPerSession = 1024 }
        );

        var session = (InMemorySession)await store.CreateAsync();
        session.SetValue("small", new byte[512]);
        // Overwriting a key must not double-count its bytes.
        session.SetValue("small", new byte[256]);
        // 256 + 768 == the cap exactly: allowed.
        session.SetValue("rest", new byte[768]);

        await Assert.That(session.UsedBytes).IsEqualTo(1024);
        await Assert
            .That(() => session.SetValue("big", new byte[2048]))
            .Throws<InvalidOperationException>();
        // The rejected write must not be partially applied.
        await Assert.That(session.TryGetValue("big", out _)).IsFalse();
        await Assert.That(session.UsedBytes).IsEqualTo(1024);
    }

    [Test]
    public async Task Load_of_idle_expired_session_returns_null()
    {
        var clock = new StoreClock();
        using var store = new InMemorySessionStore(
            new SessionOptions
            {
                IdleTimeout = TimeSpan.FromMinutes(20),
                CleanupInterval = TimeSpan.FromMinutes(5),
            }
        )
        {
            TimeProvider = clock,
        };

        var created = await store.CreateAsync();
        created.SetString("k", "v");
        await store.SaveAsync(created.Id, created);

        // A load inside the window keeps the session alive and restarts the clock.
        clock.Advance(TimeSpan.FromMinutes(19));
        await Assert.That(await store.LoadAsync(created.Id)).IsNotNull();

        // Idle past the timeout. The cleanup timer never fires in this test (real 5 minute
        // timer, test lifetime measured in milliseconds), so only the load path can notice.
        clock.Advance(TimeSpan.FromMinutes(21));
        await Assert
            .That(await store.LoadAsync(created.Id))
            .IsNull()
            .Because(
                "an idle-expired session must not stay loadable in the window before cleanup runs"
            );
        await Assert
            .That(await store.LoadAsync(created.Id))
            .IsNull()
            .Because("the expired entry is dropped on the failed load, not just hidden");
    }

    [Test]
    public async Task Regenerate_moves_data_and_drops_old_id()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        created.SetString("user", "alice");
        await store.SaveAsync(created.Id, created);

        var newId = await store.RegenerateAsync(created.Id);

        await Assert.That(newId).IsNotEqualTo(created.Id);
        await Assert
            .That(await store.LoadAsync(created.Id))
            .IsNull()
            .Because("the pre-rotation id must be dead, otherwise session fixation survives it");

        var moved = await store.LoadAsync(newId);
        await Assert.That(moved).IsNotNull();
        await Assert.That(moved!.GetString("user")).IsEqualTo("alice");
        await Assert
            .That(ReferenceEquals(moved, created))
            .IsFalse()
            .Because(
                "the old handle is still in the caller's hands; aliasing it into the new id "
                    + "would let a stale SaveAsync resurrect the pre-rotation entry"
            );
    }

    [Test]
    public async Task Regenerate_of_unknown_id_throws()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        await Assert
            .That(async () => await store.RegenerateAsync("never-saved"))
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Manual clock for the idle-expiry test. It overrides <c>GetUtcNow</c> (the store reads it for
    /// the idle deadline) and deliberately leaves <c>CreateTimer</c> on the base implementation, so
    /// advancing it cannot fire the cleanup timer — the test has to be about the load path.
    /// The epoch is non-zero so <c>LastAccessedTicks == 0</c> stays meaningless.
    /// </summary>
    private sealed class StoreClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Test]
    public async Task Concurrent_mutations_do_not_corrupt_session()
    {
        var session = new InMemorySession("s1", isNew: true);

        var writers = Enumerable
            .Range(0, 8)
            .Select(i =>
                Task.Run(() =>
                {
                    for (var j = 0; j < 200; j++)
                        session.SetValue($"k{i}_{j}", [(byte)j]);
                })
            )
            .ToArray();
        // Enumerate Keys while writers run — pre-fix this races Dictionary read vs write.
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                _ = session.Keys.ToArray();
        });
        await Task.WhenAll([.. writers, reader]);

        await Assert
            .That(session.Keys.Count())
            .IsEqualTo(8 * 200)
            .Because("no writes may be lost to dictionary corruption");
    }

    [Test]
    public async Task Live_count_tracks_adds_upserts_and_deletes()
    {
        using var store = new InMemorySessionStore(new SessionOptions { MaxSessions = 100 });

        var ids = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            var created = await store.CreateAsync();
            await store.SaveAsync(created.Id, created);
            ids.Add(created.Id);
        }

        // A second save of a live id is an upsert, not a second entry.
        var reloaded = await store.LoadAsync(ids[0]);
        await store.SaveAsync(ids[0], reloaded!);

        await Assert.That(store.LiveSessionCount).IsEqualTo(30);

        for (var i = 0; i < 10; i++)
        {
            await store.DeleteAsync(ids[i]);
        }

        // The counter is what the cap check reads, so it must agree with the table after every
        // mutation path: drift here is silent and it moves the eviction ceiling.
        await Assert.That(store.LiveSessionCount).IsEqualTo(20);
        await Assert.That(await CountLoadableAsync(store, ids)).IsEqualTo(20);
    }

    [Test]
    public async Task Live_count_drops_when_a_load_removes_an_expired_entry()
    {
        var clock = new StoreClock();
        using var store = new InMemorySessionStore(
            new SessionOptions
            {
                IdleTimeout = TimeSpan.FromMinutes(20),
                CleanupInterval = TimeSpan.FromMinutes(5),
            }
        )
        {
            TimeProvider = clock,
        };

        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var created = await store.CreateAsync();
            await store.SaveAsync(created.Id, created);
            ids.Add(created.Id);
        }

        clock.Advance(TimeSpan.FromMinutes(21));
        await Assert.That(await store.LoadAsync(ids[0])).IsNull();
        await Assert.That(await store.LoadAsync(ids[1])).IsNull();

        // Only those two failed loads removed anything: the cleanup timer never fires here.
        await Assert.That(store.LiveSessionCount).IsEqualTo(2);
    }

    [Test]
    public async Task Live_count_stays_at_the_cap_after_eviction()
    {
        using var store = new InMemorySessionStore(new SessionOptions { MaxSessions = 5 });

        var ids = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var created = await store.CreateAsync();
            created.SetString("n", i.ToString(CultureInfo.InvariantCulture));
            await store.SaveAsync(created.Id, created);
            ids.Add(created.Id);
        }

        await Assert.That(store.LiveSessionCount).IsLessThanOrEqualTo(5);
        await Assert.That(await CountLoadableAsync(store, ids)).IsEqualTo(store.LiveSessionCount);
    }

    [Test]
    public async Task Live_count_is_unchanged_by_regenerate()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var created = await store.CreateAsync();
            await store.SaveAsync(created.Id, created);
            ids.Add(created.Id);
        }

        var newId = await store.RegenerateAsync(ids[0]);

        // One removal plus one re-add: a rotation must not leak or double-count an entry.
        await Assert.That(store.LiveSessionCount).IsEqualTo(3);
        await Assert.That(await CountLoadableAsync(store, ids)).IsEqualTo(2);
        await Assert.That(await store.LoadAsync(newId)).IsNotNull();
    }

    [Test]
    public async Task Live_count_survives_concurrent_first_saves()
    {
        using var store = new InMemorySessionStore(new SessionOptions { MaxSessions = 10_000 });

        var created = new List<ISession>();
        for (var i = 0; i < 400; i++)
        {
            created.Add(await store.CreateAsync());
        }

        // The upsert is a lock-free TryAdd loop; two racers on one id must not both count it.
        await Task.WhenAll(created.Select(s => Task.Run(() => store.SaveAsync(s.Id, s).AsTask())));

        await Assert.That(store.LiveSessionCount).IsEqualTo(400);
        await Assert
            .That(await CountLoadableAsync(store, created.Select(s => s.Id)))
            .IsEqualTo(400);
    }

    private static async Task<int> CountLoadableAsync(
        InMemorySessionStore store,
        IEnumerable<string> ids
    )
    {
        var loadable = 0;
        foreach (var id in ids)
        {
            if (await store.LoadAsync(id) is not null)
            {
                loadable++;
            }
        }

        return loadable;
    }
}
