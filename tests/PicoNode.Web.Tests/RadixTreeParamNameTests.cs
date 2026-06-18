using PicoNode.Web.Internal;

namespace PicoNode.Web.Tests;

/// <summary>
/// Tests for RadixTree parameter name conflict detection.
/// Bug: RadixTree allows registering different parameter names at the same
/// path depth with different methods, causing route values to have wrong keys.
/// Fix: Validate parameter name consistency at registration time.
/// </summary>
public sealed class RadixTreeParamNameTests
{
    [Test]
    public async Task Insert_SameDepthDifferentParamNames_Throws()
    {
        var tree = new RadixTree<object>();

        // First registration succeeds
        tree.Insert("/api/{id}", "GET", new object());

        // Second registration with different param name at same depth should throw
        // BUG: currently silently succeeds with wrong param name
        try
        {
            tree.Insert("/api/{slug}", "POST", new object());
            await Assert.That(false).IsTrue(); // Should not reach here
        }
        catch (InvalidOperationException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task Insert_SameDepthSameParamName_Succeeds()
    {
        var tree = new RadixTree<object>();

        tree.Insert("/api/{id}", "GET", new object());

        // Same param name, different method — should succeed (no throw)
        tree.Insert("/api/{id}", "POST", new object());
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Insert_DifferentDepthSameParamName_Succeeds()
    {
        var tree = new RadixTree<object>();

        // Different depths — no conflict
        tree.Insert("/api/{id}", "GET", new object());

        tree.Insert("/api/{id}/details", "GET", new object());
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Insert_SameRouteDifferentMethods_SameParamName_Succeeds()
    {
        var tree = new RadixTree<object>();

        tree.Insert("/api/{id}", "GET", new object());
        tree.Insert("/api/{id}", "POST", new object());
        tree.Insert("/api/{id}", "PUT", new object());

        // All three should be registered
        var matched = tree.TryMatch("/api/123", "POST", out var result, out var values);
        await Assert.That(matched).IsTrue();
        await Assert.That(values).ContainsKey("id");
        await Assert.That(values["id"]).IsEqualTo("123");
    }
}
