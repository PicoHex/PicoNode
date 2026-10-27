namespace PicoNode;

/// <summary>UDP transport metrics snapshot with cumulative counters.</summary>
public readonly record struct UdpNodeMetrics(
    /// <summary>Total datagrams sent since node start.</summary>
    long TotalDatagramsSent,
    /// <summary>Total datagrams received since node start.</summary>
    long TotalDatagramsReceived,
    /// <summary>
    /// Total datagrams dropped instead of delivered: the dispatch queue was full or had already
    /// been closed by a shutdown.
    /// </summary>
    long TotalDatagramsDropped,
    /// <summary>Total payload bytes sent since node start.</summary>
    long TotalBytesSent,
    /// <summary>Total payload bytes received since node start.</summary>
    long TotalBytesReceived
);
