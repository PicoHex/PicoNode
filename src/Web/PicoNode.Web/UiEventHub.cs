using System.Threading.Channels;

namespace PicoNode.Web;

/// <summary>UI event with optional payload (e.g. "tokens|window" for context usage).</summary>
public sealed record UiEvent(string Name, string? Payload);

/// <summary>
/// In-process pub/sub for UI events (sessionChanged, agentChanged,
/// contextUsageChanged, ...). Subscribers receive <see cref="UiEvent"/> records.
/// </summary>
public sealed class UiEventHub
{
    /// <summary>Per-subscriber buffer (audit L6): bounded with DropOldest so a
    /// slow SSE consumer converges on the latest events instead of growing
    /// memory without limit.</summary>
    public const int ChannelCapacity = 64;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Channel<UiEvent>> _subscribers = [];

    public Guid Subscribe()
    {
        var id = Guid.NewGuid();
        lock (_gate)
            _subscribers[id] = Channel.CreateBounded<UiEvent>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                }
            );
        return id;
    }

    public ChannelReader<UiEvent> GetReader(Guid subscriptionId)
    {
        lock (_gate)
        {
            return _subscribers.TryGetValue(subscriptionId, out var channel)
                ? channel.Reader
                : throw new InvalidOperationException($"Unknown subscription {subscriptionId}");
        }
    }

    public void Unsubscribe(Guid subscriptionId)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(subscriptionId, out var channel))
                channel.Writer.TryComplete();
        }
    }

    public void Publish(string eventName) => Publish(eventName, null);

    public void Publish(string eventName, string? payload)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return;
        var evt = new UiEvent(eventName, payload);
        lock (_gate)
        {
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryWrite(evt);
        }
    }
}
