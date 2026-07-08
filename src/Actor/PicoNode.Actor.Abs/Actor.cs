namespace PicoNode.Actor.Abs;

/// <summary>
/// Base class for operational (non-ES) actors.
/// Starts a consumption loop in the constructor that reads from a Channel{Envelope}.
/// Subclasses override OnMessageAsync for command dispatch.
/// </summary>
public abstract class Actor : IActor, IAsyncDisposable
{
    private readonly Channel<Envelope> _mailbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private int _stopped;

    /// <inheritdoc/>
    public Guid Id { get; }

    protected Actor(Guid id)
    {
        Id = id;
        _mailbox = Channel.CreateUnbounded<Envelope>();
        _loopTask = RunAsync(_cts.Token);
    }

    /// <summary>Called by IActorSystem to deliver an envelope.</summary>
    internal void Post(Envelope envelope) => _mailbox.Writer.TryWrite(envelope);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in _mailbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(envelope).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Propagate failure to Ask callers; single message failure does not stop the loop
                    envelope.Tcs?.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on Stop */ }
    }

    /// <summary>
    /// Override to hook into each message (e.g., ES persistence).
    /// Default: dispatch command, return result to TCS.
    /// </summary>
    private protected virtual async ValueTask ProcessAsync(Envelope envelope)
    {
        var result = await OnMessageAsync(envelope.Command).ConfigureAwait(false);
        envelope.Tcs?.TrySetResult(result);
    }

    /// <summary>Override to dispatch commands via switch/pattern match.</summary>
    protected abstract ValueTask<object?> OnMessageAsync(ICommand command);

    /// <summary>Stop the consumption loop, wait for it to finish, and dispose resources.</summary>
    internal async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        _cts.Cancel();
        _mailbox.Writer.Complete();
        await _loopTask.ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>IAsyncDisposable — delegates to StopAsync.</summary>
    async ValueTask IAsyncDisposable.DisposeAsync() => await StopAsync();
}
