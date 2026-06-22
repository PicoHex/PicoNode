# Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add pluggable Web Session state management to PicoNode.Web with a new `PicoNode.Web.Session.Abs` abstraction package and an in-memory default implementation.

**Architecture:** New netstandard2.0 package `PicoNode.Web.Session.Abs` with `ISession`/`ISessionStore`/`SessionOptions`/`SessionExtensions`. Default `InMemorySessionStore` + `SessionMiddleware` in `PicoNode.Web`. Session ID transport via delegates. DI-managed lifecycle. AOT-first.

**Tech Stack:** .NET 10 / netstandard2.0, TUnit, PicoDI.Abs, C# 13, `Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`

## Global Constraints

- PicoNode.Web.Session.Abs targets `netstandard2.0`, PublishAot=false
- PicoNode.Web targets `net10.0`, IsAotCompatible=true, IsTrimmable=true
- All abstractions in `.Session.Abs` — no references to PicoNode.Web or PicoNode.Http
- No System.Text.Json in .Abs
- Delegates for session ID transport, not interfaces
- DI-managed lifecycle — no IDisposable on ISessionStore interface
- InMemorySessionStore is public for cross-assembly DI registration
- Lazy session creation: IsNew && !IsDirty → discard, no cookie set
- CleanupInterval configurable, Timer interval capped to IdleTimeout
- Follow existing patterns: TUnit for tests, PascalCase, sealed classes, init-only options

---

## File Structure

```
src/PicoNode.Web.Session.Abs/          ← NEW project
├── PicoNode.Web.Session.Abs.csproj
├── GlobalUsings.cs
├── ISession.cs
├── ISessionStore.cs
├── SessionOptions.cs
└── SessionExtensions.cs

src/PicoNode.Web/                       ← MODIFY existing
├── PicoNode.Web.csproj                 ← add ProjectReference to .Session.Abs
├── GlobalUsings.cs                     ← add global using
├── WebContext.cs                       ← add Session property
├── InMemorySessionStore.cs             ← NEW
├── InMemorySession.cs                  ← NEW (internal)
├── SessionMiddleware.cs                ← NEW
├── SessionIdExtractor.cs               ← NEW (delegate)
├── SessionIdSetter.cs                  ← NEW (delegate)
├── SessionCookie.cs                    ← NEW
└── SessionHeader.cs                    ← NEW

tests/PicoNode.Web.Tests/               ← MODIFY existing
├── PicoNode.Web.Tests.csproj           ← add ProjectReference to .Session.Abs
├── GlobalUsings.cs                     ← add global using
├── SessionExtensionsTests.cs           ← NEW
├── InMemorySessionStoreTests.cs        ← NEW
├── SessionMiddlewareTests.cs           ← NEW
└── SessionIntegrationTests.cs          ← NEW

PicoNode.slnx                           ← MODIFY: add new project
Directory.Packages.props                ← MODIFY: add package version
```

---

### Task 1: Scaffold PicoNode.Web.Session.Abs project

**Files:**
- Create: `src/PicoNode.Web.Session.Abs/PicoNode.Web.Session.Abs.csproj`
- Create: `src/PicoNode.Web.Session.Abs/GlobalUsings.cs`
- Modify: `PicoNode.slnx` (add project)
- Modify: `Directory.Packages.props` (add version)

**Interfaces:**
- Consumes: nothing
- Produces: compilable project skeleton, ready for interface definitions

- [ ] **Step 1: Create .csproj**

File: `src/PicoNode.Web.Session.Abs/PicoNode.Web.Session.Abs.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="PublishAot">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <PublishAot>false</PublishAot>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>true</IsPackable>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageId>PicoNode.Web.Session.Abs</PackageId>
    <Description>Core abstractions for PicoNode.Web Session: ISession, ISessionStore, and SessionOptions. Targets netstandard2.0 for maximum compatibility.</Description>
    <PackageTags>PicoHex;PicoNode;AOT;Session;Abstractions;Web</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Bcl.AsyncInterfaces" />
    <PackageReference Include="System.Buffers" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="" Visible="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create GlobalUsings.cs**

File: `src/PicoNode.Web.Session.Abs/GlobalUsings.cs`

```csharp
global using System.Buffers;
global using System.Text;
```

- [ ] **Step 3: Add project to solution**

File: `PicoNode.slnx`

Add this line inside the `<Folder Name="/src/">` element, after the `PicoNode.Abs` project:

```xml
    <Project Path="src/PicoNode.Web.Session.Abs/PicoNode.Web.Session.Abs.csproj" />
```

- [ ] **Step 4: Add package version**

File: `Directory.Packages.props`

Add inside the `<!-- PicoNode internal -->` section:

```xml
    <PackageVersion Include="PicoNode.Web.Session.Abs" Version="$(Version)" />
```

- [ ] **Step 5: Build to verify**

```bash
dotnet build src/PicoNode.Web.Session.Abs/PicoNode.Web.Session.Abs.csproj
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Web.Session.Abs/ PicoNode.slnx Directory.Packages.props
git commit -m "feat: scaffold PicoNode.Web.Session.Abs project"
```

---

### Task 2: ISession and ISessionStore interfaces

**Files:**
- Create: `src/PicoNode.Web.Session.Abs/ISession.cs`
- Create: `src/PicoNode.Web.Session.Abs/ISessionStore.cs`

**Interfaces:**
- Consumes: Task 1 (project scaffold)
- Produces: `ISession` (Id, IsNew, IsDirty, TryGetValue, SetValue, Remove, Clear, Keys), `ISessionStore` (LoadAsync, CreateAsync, SaveAsync, DeleteAsync)

- [ ] **Step 1: Create ISession.cs**

File: `src/PicoNode.Web.Session.Abs/ISession.cs`

```csharp
namespace PicoNode.Web.Session.Abs;

public interface ISession
{
    string Id { get; }

    bool IsNew { get; }

    bool IsDirty { get; }

    bool TryGetValue(string key, out byte[]? value);

    void SetValue(string key, byte[] value);

    void Remove(string key);

    void Clear();

    IEnumerable<string> Keys { get; }
}
```

- [ ] **Step 2: Create ISessionStore.cs**

File: `src/PicoNode.Web.Session.Abs/ISessionStore.cs`

```csharp
namespace PicoNode.Web.Session.Abs;

public interface ISessionStore
{
    ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default);

    ValueTask<ISession> CreateAsync(CancellationToken ct = default);

    ValueTask SaveAsync(string sessionId, ISession session, CancellationToken ct = default);

    ValueTask DeleteAsync(string sessionId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Build to verify compilation**

```bash
dotnet build src/PicoNode.Web.Session.Abs/PicoNode.Web.Session.Abs.csproj
```

Expected: Build succeeded with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode.Web.Session.Abs/ISession.cs src/PicoNode.Web.Session.Abs/ISessionStore.cs
git commit -m "feat: add ISession and ISessionStore interfaces"
```

---

### Task 3: SessionOptions

**Files:**
- Create: `src/PicoNode.Web.Session.Abs/SessionOptions.cs`
- Create: `tests/PicoNode.Web.Tests/SessionOptionsContractTests.cs`

**Interfaces:**
- Consumes: Task 1 (project scaffold)
- Produces: `SessionOptions` with `IdleTimeout` and `CleanupInterval`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/SessionOptionsContractTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class SessionOptionsContractTests
{
    [Test]
    public async Task Defaults_are_20_minutes_idle_timeout()
    {
        var options = new SessionOptions();

        await Assert
            .That(options.IdleTimeout)
            .IsEqualTo(TimeSpan.FromMinutes(20));
    }

    [Test]
    public async Task Defaults_are_5_minutes_cleanup_interval()
    {
        var options = new SessionOptions();

        await Assert
            .That(options.CleanupInterval)
            .IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task Defaults_are_not_equal()
    {
        var options = new SessionOptions();

        await Assert
            .That(options.IdleTimeout)
            .IsNotEqualTo(options.CleanupInterval);
    }

    [Test]
    public async Task Can_set_custom_values_via_init()
    {
        var options = new SessionOptions
        {
            IdleTimeout = TimeSpan.FromMinutes(60),
            CleanupInterval = TimeSpan.FromMinutes(10),
        };

        await Assert
            .That(options.IdleTimeout)
            .IsEqualTo(TimeSpan.FromMinutes(60));
        await Assert
            .That(options.CleanupInterval)
            .IsEqualTo(TimeSpan.FromMinutes(10));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionOptionsContractTests"
```

Expected: Build error — `SessionOptions` not found.

- [ ] **Step 3: Create SessionOptions.cs**

File: `src/PicoNode.Web.Session.Abs/SessionOptions.cs`

```csharp
namespace PicoNode.Web.Session.Abs;

public sealed class SessionOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);
}
```

- [ ] **Step 4: Update test project references**

File: `tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`

Add this `ProjectReference` line after the existing `PicoNode.Web` reference:

```xml
    <ProjectReference Include="..\..\src\PicoNode.Web.Session.Abs\PicoNode.Web.Session.Abs.csproj" />
```

- [ ] **Step 5: Add global using to test project**

File: `tests/PicoNode.Web.Tests/GlobalUsings.cs`

Add after existing usings:

```csharp
global using PicoNode.Web.Session.Abs;
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionOptionsContractTests"
```

Expected: 4 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PicoNode.Web.Session.Abs/SessionOptions.cs tests/PicoNode.Web.Tests/SessionOptionsContractTests.cs tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj tests/PicoNode.Web.Tests/GlobalUsings.cs
git commit -m "feat: add SessionOptions with IdleTimeout and CleanupInterval"
```

---

### Task 4: SessionExtensions

**Files:**
- Create: `src/PicoNode.Web.Session.Abs/SessionExtensions.cs`
- Create: `tests/PicoNode.Web.Tests/SessionExtensionsTests.cs`

**Interfaces:**
- Consumes: Task 2 (ISession), Task 3 (SessionOptions)
- Produces: `SessionExtensions.GetString/SetString/GetInt32/SetInt32/GetInt64/SetInt64/GetBoolean/SetBoolean`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/SessionExtensionsTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class SessionExtensionsTests
{
    private readonly TestSession _session = new();

    [Test]
    public async Task SetString_and_GetString_roundtrip()
    {
        _session.SetString("key", "hello");

        await Assert.That(_session.GetString("key")).IsEqualTo("hello");
    }

    [Test]
    public async Task GetString_returns_null_for_missing_key()
    {
        await Assert.That(_session.GetString("missing")).IsNull();
    }

    [Test]
    public async Task SetString_null_removes_key()
    {
        _session.SetString("key", "hello");
        _session.SetString("key", null);

        await Assert.That(_session.GetString("key")).IsNull();
    }

    [Test]
    public async Task SetInt32_and_GetInt32_roundtrip()
    {
        _session.SetInt32("count", 42);

        await Assert.That(_session.GetInt32("count")).IsEqualTo(42);
    }

    [Test]
    public async Task GetInt32_returns_zero_for_missing_key()
    {
        await Assert.That(_session.GetInt32("missing")).IsEqualTo(0);
    }

    [Test]
    public async Task SetInt64_and_GetInt64_roundtrip()
    {
        _session.SetInt64("big", long.MaxValue);

        await Assert.That(_session.GetInt64("big")).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task SetBoolean_and_GetBoolean_roundtrip()
    {
        _session.SetBoolean("flag", true);

        await Assert.That(_session.GetBoolean("flag")).IsTrue();
    }

    [Test]
    public async Task GetBoolean_returns_false_for_missing_key()
    {
        await Assert.That(_session.GetBoolean("missing")).IsFalse();
    }

    [Test]
    public async Task Overwrite_value_returns_latest()
    {
        _session.SetInt32("x", 1);
        _session.SetInt32("x", 99);

        await Assert.That(_session.GetInt32("x")).IsEqualTo(99);
    }

    [Test]
    public async Task Mark_sets_IsDirty_on_SetValue()
    {
        _session.SetString("key", "value");

        await Assert.That(_session.IsDirty).IsTrue();
    }

    [Test]
    public async Task Mark_sets_IsDirty_on_Remove()
    {
        _session.SetString("key", "value");
        _session.IsDirty = false;
        _session.Remove("key");

        await Assert.That(_session.IsDirty).IsTrue();
    }

    [Test]
    public async Task Mark_sets_IsDirty_on_Clear()
    {
        _session.SetString("k1", "v1");
        _session.IsDirty = false;
        _session.Clear();

        await Assert.That(_session.IsDirty).IsTrue();
    }

    [Test]
    public async Task TryGetValue_does_not_set_IsDirty()
    {
        _session.SetString("key", "value");
        _session.IsDirty = false;
        _session.TryGetValue("key", out _);

        await Assert.That(_session.IsDirty).IsFalse();
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _data = new();

        public string Id => "test-id";
        public bool IsNew => false;
        public bool IsDirty { get; set; }
        public IEnumerable<string> Keys => _data.Keys;

        public bool TryGetValue(string key, out byte[]? value) =>
            _data.TryGetValue(key, out value);

        public void SetValue(string key, byte[] value)
        {
            _data[key] = value;
            IsDirty = true;
        }

        public void Remove(string key)
        {
            _data.Remove(key);
            IsDirty = true;
        }

        public void Clear()
        {
            _data.Clear();
            IsDirty = true;
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionExtensionsTests"
```

Expected: Build errors — `SetString`, `GetString`, etc. not defined on `ISession`.

- [ ] **Step 3: Create SessionExtensions.cs**

File: `src/PicoNode.Web.Session.Abs/SessionExtensions.cs`

```csharp
namespace PicoNode.Web.Session.Abs;

public static class SessionExtensions
{
    public static string? GetString(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value)
            ? Encoding.UTF8.GetString(value)
            : null;
    }

    public static void SetString(this ISession session, string key, string? value)
    {
        if (value is null)
        {
            session.Remove(key);
            return;
        }
        session.SetValue(key, Encoding.UTF8.GetBytes(value));
    }

    public static int GetInt32(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value) && value.Length >= 4
            ? BitConverter.ToInt32(value, 0)
            : 0;
    }

    public static void SetInt32(this ISession session, string key, int value)
    {
        session.SetValue(key, BitConverter.GetBytes(value));
    }

    public static long GetInt64(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value) && value.Length >= 8
            ? BitConverter.ToInt64(value, 0)
            : 0L;
    }

    public static void SetInt64(this ISession session, string key, long value)
    {
        session.SetValue(key, BitConverter.GetBytes(value));
    }

    public static bool GetBoolean(this ISession session, string key)
    {
        return session.TryGetValue(key, out var value)
            && value.Length >= 1
            && value[0] != 0;
    }

    public static void SetBoolean(this ISession session, string key, bool value)
    {
        session.SetValue(key, [(byte)(value ? 1 : 0)]);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionExtensionsTests"
```

Expected: All 12 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Web.Session.Abs/SessionExtensions.cs tests/PicoNode.Web.Tests/SessionExtensionsTests.cs
git commit -m "feat: add SessionExtensions with typed Get/Set methods"
```

---

### Task 5: InMemorySessionStore and InMemorySession

**Files:**
- Create: `src/PicoNode.Web/InMemorySession.cs`
- Create: `src/PicoNode.Web/InMemorySessionStore.cs`
- Modify: `src/PicoNode.Web/PicoNode.Web.csproj` (add reference to .Session.Abs)
- Modify: `src/PicoNode.Web/GlobalUsings.cs` (add global using)
- Create: `tests/PicoNode.Web.Tests/InMemorySessionStoreTests.cs`

**Interfaces:**
- Consumes: Task 2 (ISession, ISessionStore), Task 3 (SessionOptions), Task 4 (SessionExtensions)
- Produces: `InMemorySessionStore` (public, ISessionStore + IDisposable), `InMemorySession` (internal, ISession)

- [ ] **Step 1: Add reference and global using to PicoNode.Web**

File: `src/PicoNode.Web/PicoNode.Web.csproj`

Add inside the `Condition="'$(UseProjectReferences)' == 'true'"` group:

```xml
    <ProjectReference Include="..\PicoNode.Web.Session.Abs\PicoNode.Web.Session.Abs.csproj" />
```

Add inside the `Condition="'$(UseProjectReferences)' != 'true'"` group:

```xml
    <PackageReference Include="PicoNode.Web.Session.Abs" />
```

File: `src/PicoNode.Web/GlobalUsings.cs`

Add after existing usings:

```csharp
global using PicoNode.Web.Session.Abs;
```

- [ ] **Step 2: Write the failing test for InMemorySessionStore**

File: `tests/PicoNode.Web.Tests/InMemorySessionStoreTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class InMemorySessionStoreTests
{
    private static SessionOptions DefaultOptions => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(20),
        CleanupInterval = TimeSpan.FromMinutes(5),
    };

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

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await store.CreateAsync()
        );
    }

    [Test]
    public async Task LoadAsync_after_dispose_throws()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await store.LoadAsync("any")
        );
    }

    [Test]
    public async Task Constructor_throws_on_zero_IdleTimeout()
    {
        await Assert
            .Throws<ArgumentOutOfRangeException>(() =>
                new InMemorySessionStore(
                    new SessionOptions { IdleTimeout = TimeSpan.Zero }
                )
            );
    }

    [Test]
    public async Task Constructor_throws_on_zero_CleanupInterval()
    {
        await Assert
            .Throws<ArgumentOutOfRangeException>(() =>
                new InMemorySessionStore(
                    new SessionOptions { CleanupInterval = TimeSpan.Zero }
                )
            );
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~InMemorySessionStoreTests"
```

Expected: Build errors — `InMemorySessionStore` not found.

- [ ] **Step 4: Create InMemorySession.cs (internal)**

File: `src/PicoNode.Web/InMemorySession.cs`

```csharp
namespace PicoNode.Web;

internal sealed class InMemorySession : ISession
{
    private readonly Dictionary<string, byte[]> _data = new();

    public InMemorySession(string id, bool isNew)
    {
        Id = id;
        IsNew = isNew;
    }

    public string Id { get; }

    public bool IsNew { get; }

    public bool IsDirty { get; private set; }

    public IEnumerable<string> Keys => _data.Keys;

    public bool TryGetValue(string key, out byte[]? value) =>
        _data.TryGetValue(key, out value);

    public void SetValue(string key, byte[] value)
    {
        _data[key] = value;
        IsDirty = true;
    }

    public void Remove(string key)
    {
        _data.Remove(key);
        IsDirty = true;
    }

    public void Clear()
    {
        _data.Clear();
        IsDirty = true;
    }
}
```

- [ ] **Step 5: Create InMemorySessionStore.cs**

File: `src/PicoNode.Web/InMemorySessionStore.cs`

```csharp
namespace PicoNode.Web;

public sealed class InMemorySessionStore : ISessionStore, IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _idleTimeout;
    private int _disposed;

    public InMemorySessionStore(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.CleanupInterval, TimeSpan.Zero);

        _idleTimeout = options.IdleTimeout;

        var interval = options.IdleTimeout < options.CleanupInterval
            ? options.IdleTimeout
            : options.CleanupInterval;

        _cleanupTimer = new Timer(
            _ => CleanupExpired(), null, interval, interval);
    }

    public ValueTask<ISession> CreateAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);

        var id = Guid.NewGuid().ToString("N");
        var session = new InMemorySession(id, isNew: true);
        _sessions[id] = new Entry { Session = session };
        return ValueTask.FromResult<ISession>(session);
    }

    public ValueTask<ISession?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);

        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            Interlocked.Exchange(
                ref entry.LastAccessedTicks,
                DateTimeOffset.UtcNow.Ticks);

            return ValueTask.FromResult<ISession?>(entry.Session);
        }

        return ValueTask.FromResult<ISession?>(null);
    }

    public ValueTask SaveAsync(
        string sessionId, ISession session, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);

        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            Interlocked.Exchange(
                ref entry.LastAccessedTicks,
                DateTimeOffset.UtcNow.Ticks);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);

        _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer.Dispose();
        _sessions.Clear();
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.Ticks - _idleTimeout.Ticks;

        foreach (var (id, entry) in _sessions)
        {
            if (Interlocked.Read(ref entry.LastAccessedTicks) < cutoff)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }

    internal sealed class Entry
    {
        public InMemorySession Session { get; init; } = null!;
        public long LastAccessedTicks;
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~InMemorySessionStoreTests"
```

Expected: All 12 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PicoNode.Web/InMemorySession.cs src/PicoNode.Web/InMemorySessionStore.cs src/PicoNode.Web/PicoNode.Web.csproj src/PicoNode.Web/GlobalUsings.cs tests/PicoNode.Web.Tests/InMemorySessionStoreTests.cs
git commit -m "feat: add InMemorySessionStore with Timer-based cleanup"
```

---

### Task 6: SessionCookie and SessionHeader delegates

**Files:**
- Create: `src/PicoNode.Web/SessionIdExtractor.cs`
- Create: `src/PicoNode.Web/SessionIdSetter.cs`
- Create: `src/PicoNode.Web/SessionCookie.cs`
- Create: `src/PicoNode.Web/SessionHeader.cs`
- Create: `tests/PicoNode.Web.Tests/SessionIdDelegateTests.cs`

**Interfaces:**
- Consumes: HttpRequest, HttpResponse (from PicoNode.Http)
- Produces: `SessionIdExtractor`, `SessionIdSetter` delegates; `SessionCookie.Create()`, `SessionHeader.Create()` returning `(SessionIdExtractor, SessionIdSetter)`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/SessionIdDelegateTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class SessionIdDelegateTests
{
    [Test]
    public async Task Cookie_extract_reads_session_cookie()
    {
        var (extract, _) = SessionCookie.Create("sid");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = "sid=abc123; other=value",
            },
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsEqualTo("abc123");
    }

    [Test]
    public async Task Cookie_extract_returns_null_when_no_cookie_header()
    {
        var (extract, _) = SessionCookie.Create("sid");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsNull();
    }

    [Test]
    public async Task Cookie_extract_returns_null_when_cookie_not_found()
    {
        var (extract, _) = SessionCookie.Create("sid");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = "other=value",
            },
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsNull();
    }

    [Test]
    public async Task Cookie_set_adds_set_cookie_header()
    {
        var (_, set) = SessionCookie.Create("sid");

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "abc123");

        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsEqualTo("sid=abc123; Path=/; HttpOnly; SameSite=Lax");
    }

    [Test]
    public async Task Cookie_set_uses_custom_cookie_name()
    {
        var (_, set) = SessionCookie.Create("mysession");

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "xyz");

        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsEqualTo("mysession=xyz; Path=/; HttpOnly; SameSite=Lax");
    }

    [Test]
    public async Task Header_extract_reads_custom_header()
    {
        var (extract, _) = SessionHeader.Create("X-Session-Id");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Session-Id"] = "abc123",
            },
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsEqualTo("abc123");
    }

    [Test]
    public async Task Header_extract_returns_null_when_header_absent()
    {
        var (extract, _) = SessionHeader.Create("X-Session-Id");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsNull();
    }

    [Test]
    public async Task Header_set_adds_response_header()
    {
        var (_, set) = SessionHeader.Create("X-Session-Id");

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "abc123");

        await Assert
            .That(response.Headers["X-Session-Id"])
            .IsEqualTo("abc123");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionIdDelegateTests"
```

Expected: Build errors — `SessionCookie`, `SessionHeader` not found.

- [ ] **Step 3: Create SessionIdExtractor.cs**

File: `src/PicoNode.Web/SessionIdExtractor.cs`

```csharp
namespace PicoNode.Web;

public delegate string? SessionIdExtractor(HttpRequest request);
```

- [ ] **Step 4: Create SessionIdSetter.cs**

File: `src/PicoNode.Web/SessionIdSetter.cs`

```csharp
namespace PicoNode.Web;

public delegate void SessionIdSetter(HttpResponse response, string sessionId);
```

- [ ] **Step 5: Create SessionCookie.cs**

File: `src/PicoNode.Web/SessionCookie.cs`

```csharp
namespace PicoNode.Web;

public static class SessionCookie
{
    public static (SessionIdExtractor Extract, SessionIdSetter Set)
        Create(string cookieName = "sid")
    {
        return (
            Extract: request =>
            {
                if (!request.Headers.TryGetValue("Cookie", out var cookieHeader))
                    return null;

                foreach (var part in cookieHeader.Split(';'))
                {
                    var trimmed = part.Trim();
                    var eq = trimmed.IndexOf('=');
                    if (eq < 0)
                        continue;

                    var name = trimmed[..eq].Trim();
                    if (!string.Equals(name, cookieName, StringComparison.Ordinal))
                        continue;

                    var value = trimmed[(eq + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }

                return null;
            },
            Set: (response, sessionId) =>
            {
                response.Headers.Add(
                    "Set-Cookie",
                    $"{cookieName}={sessionId}; Path=/; HttpOnly; SameSite=Lax");
            }
        );
    }
}
```

- [ ] **Step 6: Create SessionHeader.cs**

File: `src/PicoNode.Web/SessionHeader.cs`

```csharp
namespace PicoNode.Web;

public static class SessionHeader
{
    public static (SessionIdExtractor Extract, SessionIdSetter Set)
        Create(string headerName = "X-Session-Id")
    {
        return (
            Extract: request =>
            {
                request.Headers.TryGetValue(headerName, out var value);
                return value;
            },
            Set: (response, sessionId) =>
            {
                response.Headers.Add(headerName, sessionId);
            }
        );
    }
}
```

- [ ] **Step 7: Run test to verify it passes**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionIdDelegateTests"
```

Expected: All 8 tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/PicoNode.Web/SessionIdExtractor.cs src/PicoNode.Web/SessionIdSetter.cs src/PicoNode.Web/SessionCookie.cs src/PicoNode.Web/SessionHeader.cs tests/PicoNode.Web.Tests/SessionIdDelegateTests.cs
git commit -m "feat: add SessionCookie and SessionHeader delegate factories"
```

---

### Task 7: SessionMiddleware

**Files:**
- Create: `src/PicoNode.Web/SessionMiddleware.cs`
- Create: `tests/PicoNode.Web.Tests/SessionMiddlewareTests.cs`

**Interfaces:**
- Consumes: Task 2 (ISession, ISessionStore), Task 5 (InMemorySessionStore), Task 6 (SessionCookie, SessionHeader)
- Produces: `SessionMiddleware.Create()` — three overloads returning `WebMiddleware`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/SessionMiddlewareTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class SessionMiddlewareTests
{
    private static SessionOptions DefaultOptions => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(20),
        CleanupInterval = TimeSpan.FromMinutes(5),
    };

    private static (
        HttpRequest Request,
        WebContext Context,
        InMemorySessionStore Store
    ) CreateContext(string? cookieValue = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cookieValue is not null)
        {
            headers["Cookie"] = cookieValue;
        }

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };

        var context = WebContext.Create(request);
        var store = new InMemorySessionStore(DefaultOptions);
        return (request, context, store);
    }

    [Test]
    public async Task Middleware_creates_new_session_when_no_cookie()
    {
        var (_, context, store) = CreateContext();
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        HttpResponse? capturedResponse = null;
        WebRequestHandler handler = (ctx, ct) =>
        {
            capturedResponse = new HttpResponse { StatusCode = 200 };
            return ValueTask.FromResult(capturedResponse);
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.IsNew).IsTrue();
        // New + not dirty → no Set-Cookie
        await Assert
            .That(response.Headers.TryGetValue("Set-Cookie", out _))
            .IsFalse();
    }

    [Test]
    public async Task Middleware_persists_dirty_session_and_sets_cookie()
    {
        var (_, context, store) = CreateContext();
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("name", "value");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        // Dirty → SaveAsync called, Set-Cookie emitted
        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsNotNull();

        // Verify persistence
        var sessionId = context.Session!.Id;
        var loaded = await store.LoadAsync(sessionId);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GetString("name")).IsEqualTo("value");
    }

    [Test]
    public async Task Middleware_loads_existing_session_from_cookie()
    {
        // Pre-create a session
        var store = new InMemorySessionStore(DefaultOptions);
        var existing = await store.CreateAsync();
        existing.SetString("name", "pre-existing");
        await store.SaveAsync(existing.Id, existing);

        // Simulate request with cookie
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = $"sid={existing.Id}",
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.Id).IsEqualTo(existing.Id);
    }

    [Test]
    public async Task Middleware_skips_save_when_handler_throws()
    {
        var (_, context, store) = CreateContext();
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("data", "should-not-persist");
            throw new InvalidOperationException("handler failed");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await middleware(context, handler, CancellationToken.None));

        // Session should NOT have been persisted
        await Assert.That(context.Session).IsNotNull();
        var loaded = await store.LoadAsync(context.Session!.Id);
        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task Middleware_creates_new_session_when_load_returns_null()
    {
        var store = new InMemorySessionStore(DefaultOptions);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = "sid=expired-or-invalid-id",
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            ctx.Session!.SetString("fresh", "yes");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
        await Assert.That(context.Session!.Id).IsNotEqualTo("expired-or-invalid-id");
        await Assert.That(context.Session.IsNew).IsTrue();

        // Dirty + new → Set-Cookie with NEW id
        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsNotNull();
        await Assert
            .That(response.Headers["Set-Cookie"])
            .Contains(context.Session.Id);
    }

    [Test]
    public async Task Middleware_skips_save_for_unchanged_loaded_session()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var existing = await store.CreateAsync();
        await store.SaveAsync(existing.Id, existing);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = $"sid={existing.Id}",
        };
        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = headers,
        };
        var context = WebContext.Create(request);

        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        WebRequestHandler handler = (ctx, ct) =>
        {
            // Read-only access — no writes
            _ = ctx.Session!.GetString("any");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        };

        var response = await middleware(context, handler, CancellationToken.None);

        // IsDirty should remain false; no Set-Cookie for existing session
        await Assert.That(context.Session!.IsDirty).IsFalse();
    }

    [Test]
    public async Task Middleware_default_create_resolves_store_from_DI()
    {
        // Simulate DI by providing services scope with store registered
        // This test validates the Create() overload that uses DI

        var store = new InMemorySessionStore(DefaultOptions);

        var (_, context, _) = CreateContext();
        // Attach a scope that can resolve ISessionStore
        var scope = new TestServiceScope(store);
        context.Services = scope;

        var middleware = SessionMiddleware.Create();

        WebRequestHandler handler = (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        // Should not throw — middleware resolves store from context.Services
        await middleware(context, handler, CancellationToken.None);

        await Assert.That(context.Session).IsNotNull();
    }

    private sealed class TestServiceScope : ISvcScope
    {
        private readonly InMemorySessionStore _store;

        public TestServiceScope(InMemorySessionStore store)
        {
            _store = store;
        }

        public object GetService(Type serviceType)
        {
            if (serviceType == typeof(ISessionStore))
                return _store;
            return null!;
        }

        public IReadOnlyList<object> GetServices(Type serviceType)
        {
            var result = GetService(serviceType);
            return result is not null ? [result] : Array.Empty<object>();
        }

        public bool TryGetService(Type serviceType, out object? result)
        {
            result = GetService(serviceType);
            return result is not null;
        }

        public bool TryGetServices(Type serviceType, out IReadOnlyList<object>? result)
        {
            result = GetServices(serviceType);
            return result is not null;
        }

        public ISvcScope CreateScope() => throw new NotSupportedException();

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionMiddlewareTests"
```

Expected: Build errors — `SessionMiddleware` not found.

- [ ] **Step 3: Create SessionMiddleware.cs**

File: `src/PicoNode.Web/SessionMiddleware.cs`

```csharp
namespace PicoNode.Web;

public sealed class SessionMiddleware
{
    public static WebMiddleware Create()
    {
        return Create(
            SessionCookie.Create().Extract,
            SessionCookie.Create().Set);
    }

    public static WebMiddleware Create(
        SessionIdExtractor extractor,
        SessionIdSetter setter)
    {
        return async (context, next, ct) =>
        {
            if (!context.Services.TryGetService(
                    typeof(ISessionStore), out var svc)
                || svc is not ISessionStore store)
            {
                return await next(context, ct);
            }

            return await InvokeCoreAsync(
                context, next, ct, store, extractor, setter);
        };
    }

    public static WebMiddleware Create(
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter)
    {
        return (context, next, ct) =>
            InvokeCoreAsync(context, next, ct, store, extractor, setter);
    }

    private static async ValueTask<HttpResponse> InvokeCoreAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken ct,
        ISessionStore store,
        SessionIdExtractor extractor,
        SessionIdSetter setter)
    {
        // 1. Extract session ID
        var sessionId = extractor(context.Request);

        // 2. Load or create session
        ISession? session;
        if (sessionId is not null)
        {
            try
            {
                session = await store.LoadAsync(sessionId, ct);
            }
            catch
            {
                session = null;
            }
        }
        else
        {
            session = null;
        }

        if (session is null)
        {
            try
            {
                session = await store.CreateAsync(ct);
            }
            catch
            {
                // Proceed without session
            }
        }

        // 3. Inject into context
        context.Session = session;

        // 4. Invoke downstream
        HttpResponse response;
        try
        {
            response = await next(context, ct);
        }
        catch
        {
            // Handler threw — skip save, preserve pre-request state
            throw;
        }

        // 5. Persist if dirty
        if (session is not null && session.IsDirty)
        {
            try
            {
                await store.SaveAsync(session.Id, session, ct);
            }
            catch
            {
                // Best-effort persist — response still sent
            }
        }

        // 6. Emit session ID if new AND dirty
        if (session is not null && session.IsNew && session.IsDirty)
        {
            try
            {
                setter(response, session.Id);
            }
            catch
            {
                // Best-effort — response still sent
            }
        }

        return response;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionMiddlewareTests"
```

Expected: All 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Web/SessionMiddleware.cs tests/PicoNode.Web.Tests/SessionMiddlewareTests.cs
git commit -m "feat: add SessionMiddleware with 3 factory overloads"
```

---

### Task 8: WebContext.Session property

**Files:**
- Modify: `src/PicoNode.Web/WebContext.cs`

**Interfaces:**
- Consumes: Task 2 (ISession)
- Produces: `WebContext.Session` property

- [ ] **Step 1: Add Session property to WebContext.cs**

File: `src/PicoNode.Web/WebContext.cs`

After the existing `public ISvcScope Services { get; internal set; } = null!;` line, add:

```csharp
    public ISession? Session { get; internal set; }
```

- [ ] **Step 2: Build + run all tests to verify no regressions**

```bash
dotnet build src/PicoNode.Web/PicoNode.Web.csproj
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj
```

Expected: Build succeeded. All existing tests still PASS.

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/WebContext.cs
git commit -m "feat: add Session property to WebContext"
```

---

### Task 9: Integration tests

**Files:**
- Create: `tests/PicoNode.Web.Tests/SessionIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 7 (SessionMiddleware), Task 8 (WebContext.Session)
- Produces: End-to-end integration tests

- [ ] **Step 1: Write integration test**

File: `tests/PicoNode.Web.Tests/SessionIntegrationTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class SessionIntegrationTests
{
    private static SessionOptions DefaultOptions => new()
    {
        IdleTimeout = TimeSpan.FromMinutes(20),
        CleanupInterval = TimeSpan.FromMinutes(5),
    };

    [Test]
    public async Task WebApp_builds_with_session_middleware()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var app = new WebApp(
            new TestServiceProvider(),
            new WebAppOptions { MaxRequestBytes = 16384 }
        );

        app.Use(SessionMiddleware.Create(store,
            SessionCookie.Create().Extract,
            SessionCookie.Create().Set));

        app.MapGet("/", (ctx, ct) =>
            ValueTask.FromResult(WebResults.Text(200, "ok")));

        var handler = app.Build();

        await Assert.That(handler).IsNotNull();
    }

    [Test]
    public async Task Session_survives_multiple_middleware_invocations()
    {
        var store = new InMemorySessionStore(DefaultOptions);
        var (extract, set) = SessionCookie.Create();
        var middleware = SessionMiddleware.Create(store, extract, set);

        // Request 1: Create and write
        var req1 = new HttpRequest
        {
            Method = "GET",
            Target = "/",
        };
        var ctx1 = WebContext.Create(req1);

        await middleware(ctx1, (ctx, ct) =>
        {
            ctx.Session!.SetString("counter", "1");
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        }, CancellationToken.None);

        var sessionId = ctx1.Session!.Id;

        // Request 2: Same session, read and increment
        var req2 = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = $"sid={sessionId}",
            },
        };
        var ctx2 = WebContext.Create(req2);

        await middleware(ctx2, (ctx, ct) =>
        {
            var count = int.Parse(ctx.Session!.GetString("counter")!);
            ctx.Session.SetString("counter", (count + 1).ToString());
            return ValueTask.FromResult(
                new HttpResponse { StatusCode = 200 });
        }, CancellationToken.None);

        await Assert.That(ctx2.Session!.GetString("counter")).IsEqualTo("2");
        await Assert.That(ctx2.Session.Id).IsEqualTo(sessionId);
    }

    private sealed class NullServiceScope : ISvcScope
    {
        public object GetService(Type t) => null!;
        public IReadOnlyList<object> GetServices(Type t) => Array.Empty<object>();
        public bool TryGetService(Type t, out object? r) { r = null; return false; }
        public bool TryGetServices(Type t, out IReadOnlyList<object>? r) { r = null; return false; }
        public ISvcScope CreateScope() => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestServiceProvider : ISvcContainer
    {
        public ISvcContainer Register(SvcDescriptor d) => this;
        public bool IsRegistered(Type t) => true;
        public void Build() { }
        public ISvcScope CreateScope() => new NullServiceScope();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Run integration tests**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj --filter "FullyQualifiedName~SessionIntegrationTests"
```

Expected: All tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/PicoNode.Web.Tests/SessionIntegrationTests.cs
git commit -m "test: add session integration tests"
```

---

### Task 10: Build verification and AOT check

**Files:**
- None (verification only)

**Interfaces:**
- Consumes: All previous tasks
- Produces: Clean build, all tests pass, AOT trim warnings zero

- [ ] **Step 1: Build entire solution**

```bash
dotnet build PicoNode.slnx
```

Expected: Build succeeded with 0 errors, 0 warnings.

- [ ] **Step 2: Run full test suite**

```bash
dotnet test tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj
```

Expected: All tests PASS.

- [ ] **Step 3: Verify AOT compatibility**

```bash
dotnet publish tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -p:PublishAot=true
```

Expected: Publish succeeded with 0 errors. Any AOT trim warning would fail the build
because `IsAotCompatible=true` treats trim warnings as errors in .NET 10.

- [ ] **Step 4: Run existing tests to verify no regressions**

```bash
dotnet test tests/PicoNode.Tests/PicoNode.Tests.csproj
dotnet test tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj
```

Expected: All existing tests still PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: final build verification, all tests pass, AOT clean"
```
