namespace PicoWeb.Tests;

public sealed class ProcessShutdownTests
{
    [Test]
    public async Task WaitAsync_completes_on_cancellation()
    {
        // WaitAsync must return when the caller cancels — without waiting
        // for an actual Ctrl+C / process-exit signal.
        using var cts = new CancellationTokenSource();
        var waitTask = ProcessShutdown.WaitAsync(cts.Token);

        await cts.CancelAsync();

        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(3)));
        await Assert.That(completed == waitTask).IsTrue();
        await waitTask;
    }
}
