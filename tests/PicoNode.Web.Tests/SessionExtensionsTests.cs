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
