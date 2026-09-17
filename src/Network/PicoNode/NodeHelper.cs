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
            logger.Log(
                level,
                new EventId((int)code),
                $"Operation {operation} failed: {code}",
                exception
            );
        }
        catch
        {
            // A fault handler must never destabilize the node; swallow exceptions
            // raised by user code so they cannot escape into background tasks
            // (e.g. the TCP accept loop or TLS negotiation continuations).
        }
    }

    /// <summary>
    /// Raises a fault to every <c>OnFault</c> subscriber in isolation. A throwing
    /// subscriber must not destabilize the node's loops (same contract as
    /// <see cref="ReportFault"/>'s logger guard) and must not starve the
    /// remaining subscribers.
    /// </summary>
    internal static void RaiseFault(
        ILogger? logger,
        Action<NodeFault>? subscribers,
        NodeFault fault
    )
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<NodeFault>)subscriber)(fault);
            }
            catch (Exception ex)
            {
                try
                {
                    logger?.Log(
                        LogLevel.Warning,
                        new EventId(0),
                        "OnFault subscriber threw; exception isolated to protect the node",
                        ex
                    );
                }
                catch
                {
                    // Even the diagnostic must not destabilize the node.
                }
            }
        }
    }
}
