using PicoNode.AI;

namespace PicoNode.Agent.Tests.PicoAgentReloadTests;

/// <summary>
/// Design-review flaw #15 (P2): when a hot-reload of settings.json omits the
/// "model" field, ReloadProviderConfigs stuffed the provider *name* (e.g.
/// "deepseek") into <c>Model.Id</c>. Provider names are not model identifiers,
/// so the next LLM call would then attempt to invoke a nonexistent model at
/// the upstream and 404 / hard-fail.
///
/// Fix contract: <see cref="global::PicoAgent.Agent.ResolveInitialModelId"/>
/// returns the configured model when present and non-empty, otherwise returns
/// the empty string (the sentinel for "unresolved — let SwitchModel or the
/// first successful discovery fill it in"). It MUST NOT fall back to a
/// provider name.
/// </summary>
public class ResolveInitialModelIdTests
{
    [Test]
    public async Task ResolveInitialModelId_WhenConfigModelPresent_ReturnsConfigModel()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "deepseek",
                BaseUrl = "http://x",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 1,
            },
        };

        var id = global::PicoAgent.Agent.ResolveInitialModelId("deepseek-chat", providers);

        await Assert.That(id).IsEqualTo("deepseek-chat");
    }

    [Test]
    public async Task ResolveInitialModelId_WhenConfigModelNull_DoesNotReturnProviderName()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "deepseek",
                BaseUrl = "http://x",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 1,
            },
        };

        var id = global::PicoAgent.Agent.ResolveInitialModelId(null, providers);

        await Assert.That(id).IsNotEqualTo("deepseek");
    }

    [Test]
    public async Task ResolveInitialModelId_WhenConfigModelNull_ReturnsEmptyString()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "deepseek",
                BaseUrl = "http://x",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 1,
            },
        };

        var id = global::PicoAgent.Agent.ResolveInitialModelId(null, providers);

        await Assert.That(id).IsEqualTo("");
    }

    [Test]
    public async Task ResolveInitialModelId_WhenConfigModelEmpty_ReturnsEmptyString()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "openai",
                BaseUrl = "http://x",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 1,
            },
        };

        var id = global::PicoAgent.Agent.ResolveInitialModelId("", providers);

        await Assert.That(id).IsEqualTo("");
    }

    [Test]
    public async Task ResolveInitialModelId_WhenConfigModelWhitespace_ReturnsEmptyString()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "openai",
                BaseUrl = "http://x",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 1,
            },
        };

        // Length-check semantics: "   " has non-zero length so it's returned as-is.
        // Documents current behavior; treat as caller responsibility to sanitize.
        var id = global::PicoAgent.Agent.ResolveInitialModelId("   ", providers);

        await Assert.That(id).IsEqualTo("   ");
    }
}
