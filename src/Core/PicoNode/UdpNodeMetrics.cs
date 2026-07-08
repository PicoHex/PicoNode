namespace PicoNode;

/// <summary>UDP transport metrics snapshot with cumulative counters.</summary>
public readonly record struct UdpNodeMetrics(
    /// <summary>Total datagrams sent since node start.</summary>
    long TotalDatagramsSent,
    /// <summary>Total datagrams received since node start.</summary>
    long TotalDatagramsReceived,
    /// <summary>Total datagrams dropped due to queue overflow.</summary>
    long TotalDatagramsDropped,
    /// <summary>Total payload bytes sent since node start.</summary>
    long TotalBytesSent,
    /// <summary>Total payload bytes received since node start.</summary>
    long TotalBytesReceived
);
