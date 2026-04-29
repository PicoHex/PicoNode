namespace PicoNode.Http.Tests;

public sealed class HttpHeaderCollectionTests
{
    // ── Empty collection ──────────────────────────────────────────

    [Test]
    public async Task Empty_collection_TryGetValue_returns_false()
    {
        var headers = new HttpHeaderCollection();

        var found = headers.TryGetValue("X-Custom", out var value);

        await Assert.That(found).IsFalse();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Empty_collection_indexer_returns_null()
    {
        var headers = new HttpHeaderCollection();

        var value = headers["X-Custom"];

        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Empty_collection_Count_is_zero()
    {
        var headers = new HttpHeaderCollection();

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    // ── Single value ──────────────────────────────────────────────

    [Test]
    public async Task Single_value_TryGetValue_returns_correct_value()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");

        var found = headers.TryGetValue("Content-Type", out var value);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo("text/plain");
    }

    [Test]
    public async Task Single_value_indexer_returns_correct_value()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");

        var value = headers["Content-Type"];

        await Assert.That(value).IsEqualTo("text/plain");
    }

    // ── Case-insensitivity ────────────────────────────────────────

    [Test]
    public async Task Case_insensitive_TryGetValue_lowercase_key_matches_mixed_case_header()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");

        var found = headers.TryGetValue("content-type", out var value);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo("text/plain");
    }

    [Test]
    public async Task Case_insensitive_indexer_lowercase_key_matches_mixed_case_header()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "application/json");

        var value = headers["content-type"];

        await Assert.That(value).IsEqualTo("application/json");
    }

    // ── Duplicate headers (Set-Cookie) ────────────────────────────

    [Test]
    public async Task Duplicate_headers_GetValues_returns_all_values()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Set-Cookie", "a=1");
        headers.Add("Set-Cookie", "b=2");
        headers.Add("Set-Cookie", "c=3");

        var values = headers.GetValues("Set-Cookie");

        await Assert.That(values).IsNotNull();
        var list = values.ToList();
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(list[0]).IsEqualTo("a=1");
        await Assert.That(list[1]).IsEqualTo("b=2");
        await Assert.That(list[2]).IsEqualTo("c=3");
    }

    [Test]
    public async Task Duplicate_headers_TryGetValue_returns_first_occurrence()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Set-Cookie", "a=1");
        headers.Add("Set-Cookie", "b=2");
        headers.Add("Set-Cookie", "c=3");

        var found = headers.TryGetValue("Set-Cookie", out var value);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo("a=1");
    }

    [Test]
    public async Task Duplicate_headers_indexer_returns_first_occurrence()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Set-Cookie", "a=1");
        headers.Add("Set-Cookie", "b=2");

        var value = headers["Set-Cookie"];

        await Assert.That(value).IsEqualTo("a=1");
    }

    [Test]
    public async Task Duplicate_headers_Count_reflects_including_duplicates()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");
        headers.Add("Set-Cookie", "a=1");
        headers.Add("Set-Cookie", "b=2");

        await Assert.That(headers.Count).IsEqualTo(3);
    }

    // ── IReadOnlyList enumeration (preserves insertion order) ─────

    [Test]
    public async Task Enumeration_preserves_insertion_order()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");
        headers.Add("Set-Cookie", "a=1");
        headers.Add("Content-Length", "42");
        headers.Add("Set-Cookie", "b=2");

        var list = headers.ToList();

        await Assert.That(list.Count).IsEqualTo(4);
        await Assert.That(list[0].Key).IsEqualTo("Content-Type");
        await Assert.That(list[0].Value).IsEqualTo("text/plain");
        await Assert.That(list[1].Key).IsEqualTo("Set-Cookie");
        await Assert.That(list[1].Value).IsEqualTo("a=1");
        await Assert.That(list[2].Key).IsEqualTo("Content-Length");
        await Assert.That(list[2].Value).IsEqualTo("42");
        await Assert.That(list[3].Key).IsEqualTo("Set-Cookie");
        await Assert.That(list[3].Value).IsEqualTo("b=2");
    }

    [Test]
    public async Task IReadOnlyList_indexer_by_int_works()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");
        headers.Add("Content-Length", "42");

        var first = headers[0];
        var second = headers[1];

        await Assert.That(first.Key).IsEqualTo("Content-Type");
        await Assert.That(first.Value).IsEqualTo("text/plain");
        await Assert.That(second.Key).IsEqualTo("Content-Length");
        await Assert.That(second.Value).IsEqualTo("42");
    }

    // ── Constructor from IEnumerable<KVP> ─────────────────────────

    [Test]
    public async Task Constructor_from_enumerable_populates_all_headers()
    {
        var source = new[]
        {
            KeyValuePair.Create("Content-Type", "text/html"),
            KeyValuePair.Create("Content-Length", "100"),
            KeyValuePair.Create("Set-Cookie", "x=1"),
            KeyValuePair.Create("Set-Cookie", "y=2"),
        };

        var headers = new HttpHeaderCollection(source);

        await Assert.That(headers.Count).IsEqualTo(4);
        var found = headers.TryGetValue("content-type", out var ct);
        await Assert.That(found).IsTrue();
        await Assert.That(ct).IsEqualTo("text/html");
        await Assert.That(headers.GetValues("Set-Cookie").Count()).IsEqualTo(2);
    }

    // ── Missing key ───────────────────────────────────────────────

    [Test]
    public async Task Missing_key_TryGetValue_returns_false_and_null_value()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");

        var found = headers.TryGetValue("X-Missing", out var value);

        await Assert.That(found).IsFalse();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Missing_key_indexer_returns_null()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");

        var value = headers["Authorization"];

        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Missing_key_GetValues_returns_empty()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");

        var values = headers.GetValues("X-Missing");

        await Assert.That(values).IsNotNull();
        await Assert.That(values.Any()).IsFalse();
    }

    // ── Performance (smoke test, not a benchmark) ─────────────────

    [Test]
    public async Task Repeated_TryGetValue_on_populated_collection_is_stable()
    {
        var headers = new HttpHeaderCollection();
        for (var i = 0; i < 20; i++)
        {
            headers.Add($"X-Header-{i:D4}", $"value-{i}");
        }

        for (var iteration = 0; iteration < 1000; iteration++)
        {
            for (var i = 0; i < 20; i++)
            {
                var found = headers.TryGetValue($"x-header-{i:D4}", out var value);
                await Assert.That(found).IsTrue();
                await Assert.That(value).IsEqualTo($"value-{i}");
            }
        }
    }

    // ── GetEnumerator returns KVP ─────────────────────────────────

    [Test]
    public async Task GetEnumerator_yields_all_items()
    {
        var headers = new HttpHeaderCollection();
        headers.Add("Content-Type", "text/plain");
        headers.Add("Content-Length", "42");

        var count = 0;
        foreach (var kvp in headers)
        {
            count++;
            await Assert.That(kvp.Key).IsNotEmpty();
            await Assert.That(kvp.Value).IsNotEmpty();
        }

        await Assert.That(count).IsEqualTo(2);
    }
}
