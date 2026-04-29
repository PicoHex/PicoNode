namespace PicoNode.Web.Tests;

using PicoNode.Web.Internal;

public sealed class RadixTreeTests
{
    // ---- Exact Match ----

    [Test]
    public async Task TryMatch_returns_value_for_exact_path()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/users", "GET", 42);

        var found = tree.TryMatch("/api/users", "GET", out var value, out var routeValues);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(42);
        await Assert.That(routeValues).IsNotNull();
        await Assert.That(routeValues!.Count).IsEqualTo(0);
    }

    // ---- Root Path ----

    [Test]
    public async Task TryMatch_matches_root_path()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/", "GET", 1);

        var found = tree.TryMatch("/", "GET", out var value, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(1);
    }

    // ---- Single Parameter ----

    [Test]
    public async Task TryMatch_extracts_single_parameter()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/users/{id}", "GET", 99);

        var found = tree.TryMatch("/users/123", "GET", out var value, out var routeValues);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(99);
        await Assert.That(routeValues).IsNotNull();
        await Assert.That(routeValues!["id"]).IsEqualTo("123");
    }

    // ---- Multi-Segment Parameters ----

    [Test]
    public async Task TryMatch_extracts_multi_segment_parameters()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/users/{id}/posts/{postId}", "GET", 50);

        var found = tree.TryMatch("/users/42/posts/99", "GET", out var value, out var routeValues);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(50);
        await Assert.That(routeValues).IsNotNull();
        await Assert.That(routeValues!["id"]).IsEqualTo("42");
        await Assert.That(routeValues!["postId"]).IsEqualTo("99");
    }

    // ---- Priority: Exact Over Parameter ----

    [Test]
    public async Task TryMatch_prefers_exact_over_parameter()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/users/{id}", "GET", 1);
        tree.Insert("/users/me", "GET", 2);

        var exactResult = tree.TryMatch("/users/me", "GET", out var exactValue, out _);
        var paramResult = tree.TryMatch(
            "/users/42",
            "GET",
            out var paramValue,
            out var paramRouteValues
        );

        await Assert.That(exactResult).IsTrue();
        await Assert.That(exactValue).IsEqualTo(2);

        await Assert.That(paramResult).IsTrue();
        await Assert.That(paramValue).IsEqualTo(1);
        await Assert.That(paramRouteValues!["id"]).IsEqualTo("42");
    }

    // ---- No Match ----

    [Test]
    public async Task TryMatch_returns_false_for_unknown_path()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/users", "GET", 1);

        var found = tree.TryMatch("/nonexistent", "GET", out _, out _);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TryMatch_returns_false_for_unknown_method()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/users", "GET", 1);

        var found = tree.TryMatch("/api/users", "POST", out _, out _);

        await Assert.That(found).IsFalse();
    }

    // ---- GetMethods ----

    [Test]
    public async Task GetMethods_returns_all_methods_for_path()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/items", "GET", 1);
        tree.Insert("/api/items", "POST", 2);
        tree.Insert("/api/items", "DELETE", 3);

        var methods = tree.GetMethods("/api/items");

        await Assert.That(methods).IsNotNull();
        var list = methods.ToList();
        await Assert.That(list).Contains("GET");
        await Assert.That(list).Contains("POST");
        await Assert.That(list).Contains("DELETE");
        await Assert.That(list.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GetMethods_returns_empty_for_unknown_path()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/items", "GET", 1);

        var methods = tree.GetMethods("/nonexistent");

        await Assert.That(methods).IsNotNull();
        await Assert.That(methods.Any()).IsFalse();
    }

    // ---- Empty Tree ----

    [Test]
    public async Task TryMatch_returns_false_on_empty_tree()
    {
        var tree = new RadixTree<int>();

        var found = tree.TryMatch("/anything", "GET", out _, out _);

        await Assert.That(found).IsFalse();
    }

    // ---- Duplicate Insertion ----

    [Test]
    public async Task Insert_throws_on_duplicate_path_and_method()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/users", "GET", 1);

        await Assert
            .That(() => tree.Insert("/api/users", "GET", 2))
            .Throws<InvalidOperationException>();
    }

    // ---- Segment Count Mismatch ----

    [Test]
    public async Task TryMatch_returns_false_when_too_many_segments()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/users/{id}", "GET", 1);

        var found = tree.TryMatch("/users/42/extra", "GET", out _, out _);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TryMatch_returns_false_when_too_few_segments()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/users/{id}", "GET", 1);

        var found = tree.TryMatch("/users", "GET", out _, out _);

        await Assert.That(found).IsFalse();
    }

    // ---- Percent-Encoded Route Values (decoded via Uri.UnescapeDataString) ----

    [Test]
    public async Task TryMatch_decodes_percent_encoded_route_values()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/files/{name}", "GET", 1);

        var found = tree.TryMatch("/files/hello%20world.txt", "GET", out _, out var routeValues);

        await Assert.That(found).IsTrue();
        await Assert.That(routeValues!["name"]).IsEqualTo("hello world.txt");
    }

    // ---- Scale: 100 Routes ----

    [Test]
    public async Task TryMatch_handles_100_routes_efficiently()
    {
        var tree = new RadixTree<int>();
        const int count = 100;

        for (var i = 0; i < count; i++)
        {
            tree.Insert($"/api/item_{i}", "GET", i);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var found = tree.TryMatch($"/api/item_{i}", "GET", out var value, out _);
            await Assert.That(found).IsTrue();
            await Assert.That(value).IsEqualTo(i);
        }
        sw.Stop();

        // Non-assertive: just verify it completed quickly (sanity check only)
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
    }

    // ---- Path Without Leading Slash ----

    [Test]
    public async Task TryMatch_handles_path_without_leading_slash()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/items", "GET", 1);

        var found = tree.TryMatch("api/items", "GET", out var value, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(1);
    }

    // ---- Methodless Insert ----

    [Test]
    public async Task Insert_without_method_uses_empty_key()
    {
        var tree = new RadixTree<string>();
        tree.Insert("/health", "ok");

        var found = tree.TryMatch("/health", "", out var value, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo("ok");
    }

    // ---- Parameter Name Case Sensitivity ----

    [Test]
    public async Task TryMatch_route_values_preserve_parameter_name_case()
    {
        var tree = new RadixTree<int>();
        tree.Insert("/api/{UserId}/items/{ItemId}", "GET", 1);

        var found = tree.TryMatch("/api/abc/items/xyz", "GET", out _, out var routeValues);

        await Assert.That(found).IsTrue();
        await Assert.That(routeValues!["UserId"]).IsEqualTo("abc");
        await Assert.That(routeValues!["ItemId"]).IsEqualTo("xyz");
    }
}
