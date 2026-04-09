namespace PicoNode;

public sealed class UdpNodeMetrics
{
    internal UdpNodeMetrics(
        long totalDatagramsReceived,
        long totalDatagramsSent,
        long totalBytesReceived,
        long totalBytesSent,
        long totalDropped
    )
    {
        TotalDatagramsReceived = totalDatagramsReceived;
        TotalDatagramsSent = totalDatagramsSent;
        TotalBytesReceived = totalBytesReceived;
        TotalBytesSent = totalBytesSent;
        TotalDropped = totalDropped;
    }

    public long TotalDatagramsReceived { get; }

    public long TotalDatagramsSent { get; }

    public long TotalBytesReceived { get; }

    public long TotalBytesSent { get; }

    public long TotalDropped { get; }
}
