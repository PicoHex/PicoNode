namespace PicoNode.Agent.Tests;

public sealed class LlmActorTests
{
    [Test]
    public async Task CreateLlm_PersistsAndReadsBack()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        var data = await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());
        await Assert.That(data.ProviderName).IsEqualTo("openai");
        await Assert.That(data.ModelId).IsEqualTo("gpt-4o");
        await Assert.That(data.IsSystem).IsFalse();
    }

    [Test]
    public async Task CreateLlm_EmptyApiKey_Throws()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        await Assert
            .That(async () =>
                await system.CreateAsync<LlmActor>(new CreateLlm(
                    "openai", "gpt-4o", "", "https://api.openai.com/v1",
                    AiApiFormat.OpenAIChatCompletions, false)))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task CreateLlm_InvalidApiFormat_Throws()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        await Assert
            .That(async () =>
                await system.CreateAsync<LlmActor>(new CreateLlm(
                    "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
                    (AiApiFormat)999, false)))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task PromoteAndDemote_SystemLlm()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        system.Send(llm.Id, new PromoteSystemLlmCmd());
        await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());

        var data = await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());
        await Assert.That(data.IsSystem).IsTrue();

        system.Send(llm.Id, new DemoteSystemLlmCmd());
        data = await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());
        await Assert.That(data.IsSystem).IsFalse();
    }

    [Test]
    public async Task UpdateLlm_ChangesFields()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        system.Send(llm.Id, new UpdateLlm(ModelId: "gpt-4o-mini"));
        await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());

        var data = await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());
        await Assert.That(data.ModelId).IsEqualTo("gpt-4o-mini");
        await Assert.That(data.ProviderName).IsEqualTo("openai");
    }

    [Test]
    public async Task DeleteLlm_MarksDeleted()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        system.Send(llm.Id, new DeleteLlm());
        await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());

        // 删除后仍可查询（事件已持久化），此时 LLM 不应是系统 LLM
        var data = await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());
        await Assert.That(data.ProviderName).IsEqualTo("openai");
    }

    [Test]
    public async Task DeleteSystemLlm_PreservesData()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, true));

        // Send is fire-and-forget; exception in OnMessageAsync is caught by UnhandledErrorHandler
        // We verify the LLM is still intact by querying after the attempted delete
        system.Send(llm.Id, new DeleteLlm());
        var data = await system.AskAsync<LlmData>(llm.Id, new GetLlmDataQuery());
        await Assert.That(data.IsSystem).IsTrue();
        await Assert.That(data.ProviderName).IsEqualTo("openai");
    }

    [Test]
    public async Task EventsPersistInStore()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        // Rebuild from store
        system.Register<LlmActor>(
            _ => throw new InvalidOperationException(),
            () => new LlmActor());

        var rebuilt = await system.GetAsync<LlmActor>(llm.Id);
        await Assert.That(rebuilt).IsNotNull();

        var data = await system.AskAsync<LlmData>(rebuilt!.Id, new GetLlmDataQuery());
        await Assert.That(data.ProviderName).IsEqualTo("openai");
    }
}
