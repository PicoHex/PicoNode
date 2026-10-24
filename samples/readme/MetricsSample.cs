// The README "Metrics" block. Both transport nodes expose a snapshot type, and the UDP one is
// public (the README used to claim its counters were internal state). Compiled here and mirrored
// verbatim into all ten READMEs by tests/PicoNode.Docs.Tests.

using PicoNode;

internal static class MetricsSample
{
    public static void Dump(TcpNode tcpNode, UdpNode udpNode)
    {
        // #region readme:metrics
        // TCP
        var tcpMetrics = tcpNode.GetMetrics();
        Console.WriteLine($"Accepted: {tcpMetrics.TotalAccepted}");
        Console.WriteLine($"Active: {tcpMetrics.ActiveConnections}");
        Console.WriteLine($"Sent: {tcpMetrics.TotalBytesSent}");
        Console.WriteLine($"Received: {tcpMetrics.TotalBytesReceived}");

        // UDP
        var udpMetrics = udpNode.GetMetrics();
        Console.WriteLine($"Datagrams sent: {udpMetrics.TotalDatagramsSent}");
        Console.WriteLine($"Datagrams received: {udpMetrics.TotalDatagramsReceived}");
        Console.WriteLine($"Dropped: {udpMetrics.TotalDatagramsDropped}");
        Console.WriteLine($"Sent: {udpMetrics.TotalBytesSent}");
        Console.WriteLine($"Received: {udpMetrics.TotalBytesReceived}");
        // #endregion
    }
}
