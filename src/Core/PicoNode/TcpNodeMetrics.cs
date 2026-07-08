namespace PicoNode;

/// <summary>Transport-level metrics snapshot, including cumulative counts and current rates per second.</summary>
public readonly record struct TcpNodeMetrics(
    long TotalAccepted,
    long TotalRejected,
    long TotalClosed,
    int ActiveConnections,
    long TotalBytesSent,
    long TotalBytesReceived,
    /// <summary>Connection accepts per second since last metrics read.</summary>
    double AcceptRatePerSecond,
    /// <summary>Bytes sent per second since last metrics read.</summary>
    double BytesSentPerSecond,
    /// <summary>Bytes received per second since last metrics read.</summary>
    double BytesReceivedPerSecond
);
