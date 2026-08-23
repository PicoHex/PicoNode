namespace PicoWeb;

/// <summary>
/// Waits for a process shutdown signal (Ctrl+C / SIGINT, process exit) or
/// cancellation. Shared by <see cref="WebApiApp.RunAsync"/> and samples so
/// the signal-handling boilerplate lives in exactly one place.
/// </summary>
public static class ProcessShutdown
{
    /// <summary>
    /// Returns when the process receives Ctrl+C / SIGINT or exits, or when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var shutdownTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        ConsoleCancelEventHandler cancelHandler = (sender, e) =>
        {
            // First Ctrl+C: graceful shutdown; second Ctrl+C: force kill
            if (shutdownTcs.Task.IsCompleted)
                return;
            e.Cancel = true;
            shutdownTcs.TrySetResult();
        };
        EventHandler processExitHandler = (sender, e) => shutdownTcs.TrySetResult();

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            await Task.WhenAny(Task.Delay(Timeout.Infinite, cancellationToken), shutdownTcs.Task);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown via cancellation token
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }
    }
}
