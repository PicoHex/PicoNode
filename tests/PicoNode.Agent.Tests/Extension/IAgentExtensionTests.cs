namespace PicoNode.Agent.Tests.Extension;

public class IAgentExtensionTests
{
    [Test]
    public async Task OnToolCall_CanBlock()
    {
        var ext = new BlockingExtension();
        var blocked = await ext.OnToolCallAsync("rm", "{}"u8.ToArray(), CancellationToken.None);
        await Assert.That(blocked).IsTrue();
    }

    [Test]
    public async Task OnSystemPrompt_CanModify()
    {
        var ext = new ModifyingExtension();
        var prompt = await ext.OnSystemPromptAsync("base prompt");
        await Assert.That(prompt).Contains("base prompt");
        await Assert.That(prompt).Contains("extra rules");
    }

    private sealed class BlockingExtension : IAgentExtension
    {
        public Task<bool> OnToolCallAsync(string n, byte[] a, CancellationToken ct) =>
            Task.FromResult(n == "rm");

        public Task<string?> OnSystemPromptAsync(string c) => Task.FromResult<string?>(null);

        public Task<byte[]?> OnToolResultAsync(string n, byte[] r, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
    }

    private sealed class ModifyingExtension : IAgentExtension
    {
        public Task<bool> OnToolCallAsync(string n, byte[] a, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<string?> OnSystemPromptAsync(string c) =>
            Task.FromResult<string?>($"{c}\nextra rules");

        public Task<byte[]?> OnToolResultAsync(string n, byte[] r, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
    }
}
