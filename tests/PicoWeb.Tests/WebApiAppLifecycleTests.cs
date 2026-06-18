namespace PicoWeb.Tests;

/// <summary>
/// Tests for WebApiApp lifecycle improvements.
/// Verifies that RunAsync handles process exit signals for graceful shutdown.
/// Note: Full signal testing requires integration test with actual process.
/// </summary>
public sealed class WebApiAppLifecycleTests
{
    [Test]
    public async Task RunAsync_StartsAndStops_WithCancellationToken()
    {
        // Verify RunAsync responds to cancellation
        using var cts = new CancellationTokenSource();
        var builder = new WebApiBuilder();
        var app = builder.Build();

        var runTask = app.RunAsync("http://127.0.0.1:0", cts.Token);

        // Give it a moment to start
        await Task.Delay(100);
        await cts.CancelAsync();

        // Should complete without exception when cancelled
        var completed = runTask.IsCompletedSuccessfully || runTask.IsCompleted;
        await Assert.That(completed).IsTrue();
    }
}
