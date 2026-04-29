namespace PicoNode.Abs;

/// <summary>Represents a transport node (TCP or UDP) with lifecycle management.</summary>
public interface INode
{
    /// <summary>Gets the local endpoint this node is bound to.</summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>Gets the current state of the node (e.g. Running, Stopped).</summary>
    NodeState State { get; }

    /// <summary>Starts the node, begins accepting connections or datagrams.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Gracefully stops the node, draining in-flight work up to the configured drain timeout.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
