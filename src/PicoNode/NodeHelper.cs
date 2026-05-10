namespace PicoNode;

internal static class NodeHelper
{
    internal static void ReportFault(
        ILogger? logger,
        NodeFaultCode code,
        string operation,
        Exception? exception = null
    )
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            var level = NodeFaultLogLevelMapper.GetLevel(code);
            logger.Log(level, new EventId((int)code), $"Operation {operation} failed: {code}", exception);
        }
        catch
        {
            // A fault handler must never destabilize the node; swallow exceptions
            // raised by user code so they cannot escape into background tasks
            // (e.g. the TCP accept loop or TLS negotiation continuations).
        }
    }
}
