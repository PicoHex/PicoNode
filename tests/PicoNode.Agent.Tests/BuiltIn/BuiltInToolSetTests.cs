namespace PicoNode.Agent.Tests.BuiltIn;

public class BuiltInToolSetTests
{
    [Test]
    public async Task Register_ThenFind_ReturnsTool()
    {
        var set = new BuiltInToolSet();
        set.Register(new StubTool("test", "desc"));
        await Assert.That(set.Find("test")).IsNotNull();
    }

    [Test]
    public async Task ApplyCodingPreset_EnablesReadBashEditWrite()
    {
        var set = new BuiltInToolSet();
        set.Register(new StubTool("read", "r"));
        set.Register(new StubTool("bash", "b"));
        set.Register(new StubTool("edit", "e"));
        set.Register(new StubTool("write", "w"));
        set.Register(new StubTool("grep", "g"));

        set.ApplyPreset(ToolPreset.Coding);

        await Assert.That(set.Find("read")).IsNotNull();
        await Assert.That(set.Find("bash")).IsNotNull();
        await Assert.That(set.Find("edit")).IsNotNull();
        await Assert.That(set.Find("write")).IsNotNull();
        await Assert.That(set.Find("grep")).IsNull();
    }

    [Test]
    public async Task DisableThenFind_ReturnsNull()
    {
        var set = new BuiltInToolSet();
        set.Register(new StubTool("read", "r"));
        set.Disable("read");
        await Assert.That(set.Find("read")).IsNull();
    }

    [Test]
    public async Task GetActiveTools_ReturnsOnlyEnabled()
    {
        var set = new BuiltInToolSet();
        set.Register(new StubTool("a", "a"));
        set.Register(new StubTool("b", "b"));
        set.Disable("b");
        var active = set.GetActiveTools().ToList();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].Name).IsEqualTo("a");
    }

    private sealed class StubTool : IBuiltInTool
    {
        public string Name { get; }
        public string Description { get; }
        public string? InputSchema => null;

        public StubTool(string name, string desc) { Name = name; Description = desc; }

        public Task<(string, bool)> ExecuteAsync(
            IReadOnlyDictionary<string, object?> args,
            string wd,
            CancellationToken ct) => Task.FromResult(("ok", false));
    }

    [Test]
    public async Task FormatForSystemPrompt_IncludesParameters()
    {
        var set = new BuiltInToolSet();
        set.Register(new ReadTool());
        set.Register(new WriteTool());

        var prompt = set.FormatForSystemPrompt();

        // Read tool should show parameters
        await Assert.That(prompt).Contains("path (string, required)");
        await Assert.That(prompt).Contains("offset (integer)");
        await Assert.That(prompt).Contains("limit (integer)");

        // Write tool should show parameters
        await Assert.That(prompt).Contains("content (string, required)");
    }
}
