namespace PicoNode.Abs;

/// <summary>Behavior when the UDP dispatch queue is full.</summary>
public enum UdpOverflowMode
{
    /// <summary>When the dispatch queue is full, drop the newest datagram.</summary>
    DropNewest,

    /// <summary>When the dispatch queue is full, block the receive loop until space is available.</summary>
    Wait,
}
