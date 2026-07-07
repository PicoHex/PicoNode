namespace PicoNode.Agent.Tests;

public class ToolTests
{
    [Test]
    public async Task Equality_SameName_AreEqual()
    {
        var a = new Tool
        {
            Name = "bash",
            Description = "Execute shell commands",
            Kind = ToolKind.BuiltIn,
        };
        var b = new Tool
        {
            Name = "bash",
            Description = "Different description",
            Kind = ToolKind.Capability,
        };
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equality_DifferentName_AreNotEqual()
    {
        var a = new Tool { Name = "read" };
        var b = new Tool { Name = "write" };
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_SameName_SameHash()
    {
        var a = new Tool { Name = "read", Description = "x" };
        var b = new Tool { Name = "read", Description = "y" };
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
