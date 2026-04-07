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
            faultHandler(new NodeFault(code, operation, exception));
        }
        catch { }
    }
}
