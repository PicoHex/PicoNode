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
    public async Task LoadAsync_returns_null_for_unknown_id()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var session = await store.LoadAsync("nonexistent");

        await Assert.That(session).IsNull();
    }

    [Test]
    public async Task LoadAsync_returns_created_session()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
        var loaded = await store.LoadAsync(created.Id);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Id).IsEqualTo(created.Id);
    }

    [Test]
    public async Task LoadAsync_loaded_session_IsNew_is_false()
    {
        using var store = new InMemorySessionStore(DefaultOptions);

        var created = await store.CreateAsync();
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
}
