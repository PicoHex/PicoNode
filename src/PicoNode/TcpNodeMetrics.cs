namespace PicoNode;

public sealed class TcpNodeMetrics
{
    internal TcpNodeMetrics(
        long totalAccepted,
        long totalRejected,
        long totalClosed,
        int activeConnections,
        long totalBytesSent,
        long totalBytesReceived
    )
    {
        TotalAccepted = totalAccepted;
        TotalRejected = totalRejected;
        TotalClosed = totalClosed;
        ActiveConnections = activeConnections;
        TotalBytesSent = totalBytesSent;
        TotalBytesReceived = totalBytesReceived;
    }

    public long TotalAccepted { get; }

    public long TotalRejected { get; }

    public long TotalClosed { get; }

    public int ActiveConnections { get; }

    public long TotalBytesSent { get; }

    public long TotalBytesReceived { get; }
}
