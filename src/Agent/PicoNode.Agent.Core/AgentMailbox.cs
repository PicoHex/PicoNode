using System.Threading.Channels;

namespace PicoNode.Agent.Domain;

public sealed class AgentMailbox<T> : IDisposable
{
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cts;

    public AgentMailbox(Func<T, CancellationToken, Task> handler)
    {
        _channel = Channel.CreateUnbounded<T>();
        _cts = new CancellationTokenSource();
        _ = RunAsync(handler);
    }

    public void Post(T message) => _channel.Writer.TryWrite(message);

    private async Task RunAsync(Func<T, CancellationToken, Task> handler)
    {
        await foreach (var msg in _channel.Reader.ReadAllAsync(_cts.Token))
        {
            await handler(msg, _cts.Token);
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}
