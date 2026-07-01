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

        // RunAsync must complete within a bounded time after cancellation —
        // previously this checked `runTask.IsCompleted` synchronously right
        // after Cancel(), which only passed by accident when StartAsync had
        // already faulted (e.g. bind-to-port-80 permission failure). With
        // real ephemeral binding the shutdown is genuinely async, so we
        // must await it.
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(3)));
        await Assert.That(completed == runTask).IsTrue();
        await runTask;
    }
}
