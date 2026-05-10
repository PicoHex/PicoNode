namespace PicoNode;

public static class NodeFaultLogLevelMapper
{
    public static LogLevel GetLevel(NodeFaultCode code) => code switch
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
        NodeFaultCode.TlsFailed => LogLevel.Error,
    };
}
