namespace PicoNode.Abs;

/// <summary>Lifecycle state of a transport node (TCP or UDP).</summary>
public enum NodeState
{
    /// <summary>Node constructed but not yet started.</summary>
    Created,

    /// <summary>Node is binding and starting accept/receive loops.</summary>
    Starting,

    /// <summary>Node is accepting connections or datagrams normally.</summary>
    Running,

    /// <summary>Node is draining in-flight work before stopping.</summary>
    Stopping,

    /// <summary>Node has completed shutdown; all resources released.</summary>
    Stopped,

    /// <summary>Node has been disposed; cannot be restarted.</summary>
    Disposed,
}
