using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace PicoNode.Web;

/// <summary>
/// UI event with optional payload (e.g. "tokens|window" for context usage) and an optional
/// publisher cursor (<see cref="Epoch"/>, <see cref="Seq"/>) that an SSE writer turns into the
/// frame's <c>id: {Epoch}:{Seq}</c> so a reconnecting consumer can say which frames it missed.
/// </summary>
/// <remarks>
/// <para>Both cursor members default to <c>0 = "not provided"</c>, so
/// <c>new UiEvent("sessionChanged", "abc")</c> still compiles and an event nobody minted for
/// (in particular the <see cref="UiEvent"/> instances handed to subscribers before this cursor
/// existed) behaves exactly as it did: a consumer that ignores the two members reads the same
/// <see cref="Name"/> and <see cref="Payload"/> as before. <see cref="UiEventHub"/> always mints
/// the pair on delivery, so a delivered event never carries <c>Seq == 0</c>.</para>
/// <para>The members are part of record equality, so two events that differ only by their cursor
/// are no longer equal. Compare <see cref="Name"/>/<see cref="Payload"/> for content.</para>
/// </remarks>
public sealed record UiEvent(string Name, string? Payload)
{
    /// <summary>Cursor half 1: the generation (process lifetime) of the publisher, in unix
    /// milliseconds. Changes on every restart, so a cursor from a dead process cannot be mistaken
    /// for a position in this one. <c>0</c> = not provided.</summary>
    public long Epoch { get; init; }

    /// <summary>Cursor half 2: the position of this event within its <see cref="Epoch"/>, strictly
    /// increasing per hub instance and starting at 1. Delivery order equals seq order.
    /// <c>0</c> = not provided.</summary>
    public long Seq { get; init; }
}

/// <summary>
/// In-process pub/sub for UI events (sessionChanged, agentChanged,
/// contextUsageChanged, ...). Subscribers receive <see cref="UiEvent"/> records.
/// </summary>
/// <remarks>
/// <para>The hub is the publisher, so the hub mints the cursor: <see cref="Publish(UiEvent)"/>
/// stamps <see cref="UiEvent.Epoch"/> with this process's generation and
/// <see cref="UiEvent.Seq"/> with the hub's next number, under the same lock it delivers under.
/// Because minting and delivery are serialised by that one lock, the seq a subscriber observes is
/// monotonically increasing across the lifetime of the hub and across separate subscriptions
/// (reconnects), not per connection, and it never skips relative to what was delivered. A
/// subscriber that is dropped in (bounded channel, <see cref="ChannelCapacity"/>) loses events and
/// therefore sees a gap in the cursor - which is exactly the signal the consumer wants.</para>
/// </remarks>
public sealed class UiEventHub
{
    /// <summary>Per-subscriber buffer (audit L6): bounded with DropOldest so a
    /// slow SSE consumer converges on the latest events instead of growing
    /// memory without limit.</summary>
    public const int ChannelCapacity = 64;

    /// <summary>The generation of this process: the unix-millisecond instant at which it started,
    /// read once from the OS. A <c>static readonly</c> because the epoch's unit is the process
    /// lifetime: every <see cref="UiEventHub"/> in the process reports the same value, so the two
    /// SSE channels (session body and shell notifications) stay comparable, while a new process
    /// gets a new stamp and its <c>Seq == 1</c> cannot be mistaken for a healed gap. Not a
    /// cache-shared constant: it is evaluated at type initialisation, once per process.</summary>
    private static readonly long ProcessEpoch = ResolveProcessEpoch();

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Channel<UiEvent>> _subscribers = [];

    /// <summary>Last seq handed out to a <em>hub-minted</em> event. Guarded by <see cref="_gate"/>,
    /// which is what makes seq order equal delivery order.</summary>
    private long _seq;

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

    public void Publish(string eventName, string? payload) => Publish(new UiEvent(eventName, payload));

    /// <summary>
    /// Delivers <paramref name="evt"/> to every current subscriber. Who wins the cursor:
    /// <list type="bullet">
    /// <item><description>both halves non-zero (a caller that already owns a cursor, e.g. an event
    /// relayed from another generation): delivered <em>unchanged</em>, and the hub's counter is not
    /// consumed - a foreign cursor must not punch a hole into this hub's own numbering;</description></item>
    /// <item><description>either half zero, i.e. not provided (this includes half a cursor): the hub
    /// mints <em>both</em> halves - <see cref="UiEvent.Epoch"/> = the generation of this process,
    /// <see cref="UiEvent.Seq"/> = the hub's next number, starting at 1. A lone half is discarded
    /// rather than completed, because completing it would fabricate a position ("generation 42, this
    /// hub's n-th event") that never existed.</description></item>
    /// </list>
    /// A blank <see cref="UiEvent.Name"/> is not an event: nothing is delivered and no seq is
    /// consumed, so no subscriber can ever be missing a cursor it was entitled to.
    /// </summary>
    public void Publish(UiEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.Name))
            return;

        lock (_gate)
        {
            // Mint inside the delivering lock: one counter, one writer, so the seq a subscriber
            // sees is monotonic across connections and matches the delivery order exactly.
            var delivered =
                evt.Epoch != 0 && evt.Seq != 0
                    ? evt
                    : evt with
                    {
                        Epoch = ProcessEpoch,
                        Seq = ++_seq,
                    };
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryWrite(delivered);
        }
    }

    /// <summary>Unix milliseconds at which this process started. <see cref="Process"/> reads that
    /// from the OS; a host that does not expose it (a restricted or unsupported platform) falls
    /// back to the wall clock at type initialisation, which keeps every property this value must
    /// have - non-zero, fixed for the process, different after a restart - because it is a
    /// <c>static</c> initialised once per process. <see cref="Process"/> rather than a timestamp
    /// taken at type initialisation, because the type is initialised lazily: on a long-lived
    /// process the two can differ by hours.</summary>
    private static long ResolveProcessEpoch()
    {
        try
        {
            var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            return new DateTimeOffset(started, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        catch (Exception ex)
            when (ex is PlatformNotSupportedException
                or NotSupportedException
                or InvalidOperationException
                or Win32Exception
            )
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
