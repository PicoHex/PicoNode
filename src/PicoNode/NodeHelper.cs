namespace PicoNode;

internal static class NodeHelper
{
    internal static void ReportFault(
        Action<NodeFault>? faultHandler,
        NodeFaultCode code,
        string operation,
        Exception? exception = null
    )
    {
        if (faultHandler is null)
        {
            return;
        }

        try
        {
            faultHandler.Invoke(new NodeFault(code, operation, exception));
        }
        catch
        {
            // A fault handler must never destabilize the node; swallow exceptions
            // raised by user code so they cannot escape into background tasks
            // (e.g. the TCP accept loop or TLS negotiation continuations).
        }
    }
}
