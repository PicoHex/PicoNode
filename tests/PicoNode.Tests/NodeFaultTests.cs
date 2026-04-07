namespace PicoNode.Tests;

public sealed class NodeFaultTests
{
    [Test]
    public async Task Constructor_sets_all_properties()
    {
        var exception = new InvalidOperationException("boom");
        var fault = new NodeFault(NodeFaultCode.ReceiveFailed, "tcp.receive", exception);

        await Assert.That(fault.Code).IsEqualTo(NodeFaultCode.ReceiveFailed);
        await Assert.That(fault.Operation).IsEqualTo("tcp.receive");
        await Assert.That(fault.Exception).IsSameReferenceAs(exception);
    }

    [Test]
    public async Task Constructor_allows_null_exception()
    {
        var fault = new NodeFault(NodeFaultCode.StartFailed, "node.start");

        await Assert.That(fault.Code).IsEqualTo(NodeFaultCode.StartFailed);
        await Assert.That(fault.Operation).IsEqualTo("node.start");
        await Assert.That(fault.Exception).IsNull();
    }

    [Test]
    public async Task Constructor_rejects_null_operation()
    {
        await Assert
            .That(() => new NodeFault(NodeFaultCode.StopFailed, null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Equal_values_compare_equal()
    {
        var exception = new InvalidOperationException("same");
        var left = new NodeFault(NodeFaultCode.HandlerFailed, "tcp.handler", exception);
        var right = new NodeFault(NodeFaultCode.HandlerFailed, "tcp.handler", exception);

        await Assert.That(left.Equals(right)).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    [Test]
    public async Task Different_values_compare_not_equal()
    {
        var left = new NodeFault(NodeFaultCode.HandlerFailed, "tcp.handler");
        var right = new NodeFault(NodeFaultCode.SendFailed, "tcp.send");

        await Assert.That(left.Equals(right)).IsFalse();
    }

    [Test]
    public async Task Struct_is_readonly()
    {
        var type = typeof(NodeFault);

        await Assert.That(type.IsValueType).IsTrue();

        var writableProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(x => x.SetMethod is not null)
            .Select(x => x.Name)
            .ToArray();

        await Assert.That(writableProperties.Length).IsEqualTo(0);
    }
}
