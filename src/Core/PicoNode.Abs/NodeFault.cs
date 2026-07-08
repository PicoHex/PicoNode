namespace PicoNode.Abs;

public readonly record struct NodeFault(
    NodeFaultCode Code,
    string Operation,
    Exception? Exception = null
)
{
    public NodeFaultCode Code { get; } = Code;

    public string Operation { get; } =
        Operation ?? throw new ArgumentNullException(nameof(Operation));

    public Exception? Exception { get; } = Exception;
}
