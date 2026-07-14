namespace PicoNode.Agent.Tests;

using DomainAgent = PicoNode.Agent.Domain.Agent;

public sealed class AgentSkillsPersistTests
{
    [Test]
    public async Task Skills_SurviveRebuild()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(cmd => cmd switch
        {
            CreateAgent c => new DomainAgent(c),
            _ => throw new InvalidOperationException(),
        });

        var agent = await system.CreateAsync<DomainAgent>(new CreateAgent(
            [new Llm { ProviderName = "p", ModelId = "m", ApiKey = "sk-test" }],
            "p", "m", ParentId: null, Packages: null, Name: "TestAgent"));

        // Add skills via LearnSkill (event-sourced)
        system.Send(agent.Id, new LearnSkill(new SkillInfo { Name = "write-tests", Description = "Write tests" }));
        system.Send(agent.Id, new LearnSkill(new SkillInfo { Name = "read-files", Description = "Read files" }));
        // Force ordering
        await system.AskAsync<string>(agent.Id, new GetAgentNameQuery());

        // Rebuild agent via GetAsync (simulates restart)
        var rebuilt = await system.GetAsync<DomainAgent>(agent.Id);
        await Assert.That(rebuilt).IsNotNull();

        // Verify skills survive rebuild
        var snap = await system.AskAsync<AgentConfigSnapshot>(rebuilt!.Id, new GetConfigQuery());
        await Assert.That(snap.Skills).HasCount(2);
        await Assert.That(snap.Skills.Any(s => s.Name == "write-tests")).IsTrue();
    }
}
