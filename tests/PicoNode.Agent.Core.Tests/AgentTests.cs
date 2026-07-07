namespace PicoNode.Agent.Tests;

public class AgentTests
{
    [Test]
    public async Task Ctor_EmptyLlms_Throws()
    {
        await Assert
            .That(() => new Domain.Agent(Guid.CreateVersion7(), [], "any", "any", "/tmp"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Ctor_ApiKeyMissing_Throws()
    {
        var llms = new List<Llm>
        {
            new()
            {
                ProviderName = "test",
                ModelId = "test",
                ApiKey = "",
            },
        };
        await Assert
            .That(() => new Domain.Agent(Guid.CreateVersion7(), llms, "test", "test", "/tmp"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Ctor_CurrentLlmNotInList_Throws()
    {
        var llms = new List<Llm>
        {
            new()
            {
                ProviderName = "deepseek",
                ModelId = "chat",
                ApiKey = "sk-xxx",
            },
        };
        await Assert
            .That(() => new Domain.Agent(Guid.CreateVersion7(), llms, "openai", "gpt", "/tmp"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Ctor_Valid_PendingStatus()
    {
        var agent = CreateAgent();
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Pending);
        await Assert.That(agent.Session).IsNotNull();
    }

    [Test]
    public async Task Start_FromPending_TransitionsToRunning()
    {
        var agent = CreateAgent();
        agent.Start();
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task Start_FromRunning_Throws()
    {
        var agent = CreateAgent();
        agent.Start();
        await Assert.That(() => agent.Start()).Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Complete_FromRunning_TransitionsToCompleted()
    {
        var agent = CreateAgent();
        agent.Start();
        agent.Complete();
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Completed);
    }

    [Test]
    public async Task Fail_FromRunning_TransitionsToFailed()
    {
        var agent = CreateAgent();
        agent.Start();
        agent.Fail("network error");
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Failed);
    }

    [Test]
    public async Task Fail_StoresReason()
    {
        var agent = CreateAgent();
        agent.Start();
        agent.Fail("network timeout");
        await Assert.That(agent.FailureReason).IsEqualTo("network timeout");
    }

    [Test]
    public async Task Complete_FromPending_Throws()
    {
        var agent = CreateAgent();
        await Assert.That(() => agent.Complete()).Throws<DomainInvariantException>();
    }

    [Test]
    public async Task RemoveLlm_OnlyOne_Throws()
    {
        var agent = CreateAgent();
        await Assert
            .That(() => agent.RemoveLlm("deepseek", "deepseek-chat"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task RemoveLlm_CurrentLlm_Throws()
    {
        var agent = CreateAgent();
        agent.AddLlm(
            new Llm
            {
                ProviderName = "openai",
                ModelId = "gpt-4o",
                ApiKey = "sk-xxx",
            }
        );
        await Assert
            .That(() => agent.RemoveLlm("deepseek", "deepseek-chat"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task RemoveLlm_AfterSwitch_Succeeds()
    {
        var agent = CreateAgent();
        agent.AddLlm(
            new Llm
            {
                ProviderName = "openai",
                ModelId = "gpt-4o",
                ApiKey = "sk-xxx",
            }
        );
        agent.SwitchLlm("openai", "gpt-4o");
        agent.RemoveLlm("deepseek", "deepseek-chat");
        await Assert.That(agent.Llms.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SwitchLlm_NotFound_Throws()
    {
        var agent = CreateAgent();
        await Assert
            .That(() => agent.SwitchLlm("nonexistent", "model"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task AddLlm_NoApiKey_Throws()
    {
        var agent = CreateAgent();
        await Assert
            .That(() => agent.AddLlm(new Llm { ProviderName = "x", ModelId = "y" }))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task AddTool_DuplicateName_Throws()
    {
        var agent = CreateAgent();
        agent.AddTool(new Tool { Name = "read", Description = "Read files" });
        await Assert
            .That(() => agent.AddTool(new Tool { Name = "read", Description = "Duplicate" }))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task RemoveTool_ByName()
    {
        var agent = CreateAgent();
        agent.AddTool(new Tool { Name = "read", Description = "Read" });
        agent.RemoveTool("read");
        await Assert.That(agent.Tools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SpawnChild_SetsParentAndStatus()
    {
        var parent = CreateAgent();
        var childLlms = new List<Llm>
        {
            new()
            {
                ProviderName = "openai",
                ModelId = "gpt-4o",
                ApiKey = "sk-xxx",
            },
        };
        var child = parent.SpawnChild(childLlms, "openai", "gpt-4o", []);
        await Assert.That(parent.ChildIds).Contains(child.Id);
        await Assert.That(child.ParentId).IsEqualTo(parent.Id);
        await Assert.That(child.Status).IsEqualTo(AgentStatus.Pending);
    }

    private static Domain.Agent CreateAgent()
    {
        var llms = new List<Llm>
        {
            new()
            {
                ProviderName = "deepseek",
                ModelId = "deepseek-chat",
                ApiKey = "sk-test",
            },
        };
        return new Domain.Agent(
            Guid.CreateVersion7(),
            llms,
            "deepseek",
            "deepseek-chat",
            "/tmp/test"
        );
    }
}
