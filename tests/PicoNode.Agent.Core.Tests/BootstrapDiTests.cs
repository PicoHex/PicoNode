using PicoDI;

namespace PicoNode.Agent.Core.Tests;

public sealed class BootstrapDiTests
{
    [Test]
    public async Task ServiceRegistration_Resolves_LoggerFactory()
    {
        var container = new SvcContainer();
        BootstrapServices.Configure(container, home: null!, new AgentConfig());
        container.Build();

        await using var scope = container.CreateScope();
        var factory = (ILoggerFactory)scope.GetService(typeof(ILoggerFactory));

        await Assert.That(factory).IsNotNull();
        var logger = factory.CreateLogger("test");
        await Assert.That(logger).IsNotNull();
    }

    [Test]
    public async Task ServiceRegistration_Resolves_HttpClient()
    {
        var container = new SvcContainer();
        BootstrapServices.Configure(container, home: null!, new AgentConfig());
        container.Build();

        await using var scope = container.CreateScope();
        var http = (HttpClient)scope.GetService(typeof(HttpClient));

        await Assert.That(http).IsNotNull();
    }

    [Test]
    public async Task ServiceRegistration_Resolves_ActorSystem()
    {
        var container = new SvcContainer();
        BootstrapServices.Configure(container, home: null!, new AgentConfig());
        container.Build();

        await using var scope = container.CreateScope();
        var system = (ActorSystem)scope.GetService(typeof(ActorSystem));

        await Assert.That(system).IsNotNull();
    }
}
