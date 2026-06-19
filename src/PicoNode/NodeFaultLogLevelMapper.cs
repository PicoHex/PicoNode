using System.Collections.Concurrent;

namespace PicoNode;

public static class NodeFaultLogLevelMapper
{
    private static readonly ConcurrentDictionary<NodeFaultCode, LogLevel> _overrides = new();

    /// <summary>Override the log level for a specific fault code at runtime.</summary>
    public static void Override(NodeFaultCode code, LogLevel level) => _overrides[code] = level;

    /// <summary>Remove a runtime override, reverting to the default mapping.</summary>
    public static void Reset(NodeFaultCode code) => _overrides.TryRemove(code, out _);

    /// <summary>Remove all runtime overrides, reverting to default mappings.</summary>
    public static void ResetAll() => _overrides.Clear();

    public static LogLevel GetLevel(NodeFaultCode code)
    {
        if (_overrides.TryGetValue(code, out var level))
            return level;

        return code switch
        {
            NodeFaultCode.StartFailed => LogLevel.Error,
            NodeFaultCode.StopFailed => LogLevel.Error,
            NodeFaultCode.AcceptFailed => LogLevel.Error,
            NodeFaultCode.SessionRejected => LogLevel.Warning,
            NodeFaultCode.ReceiveFailed => LogLevel.Error,
            NodeFaultCode.SendFailed => LogLevel.Error,
            NodeFaultCode.HandlerFailed => LogLevel.Error,
            NodeFaultCode.DatagramReceiveFailed => LogLevel.Error,
            NodeFaultCode.DatagramDropped => LogLevel.Warning,
            NodeFaultCode.DatagramHandlerFailed => LogLevel.Error,
            NodeFaultCode.TlsFailed => LogLevel.Debug,
        };
    }
}
