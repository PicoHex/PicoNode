using PicoDI;
using PicoDI.Abs;
using PicoLog.Abs;
using PicoNode.Actor.Abs;

namespace PicoNode.Actor;

/// <summary>PicoDI registration extensions for PicoNode.Actor.</summary>
public static class PicoActorDiExtensions
{
    /// <summary>
    /// Registers PicoNode.Actor services in the DI container.
    /// IEventStore defaults to <see cref="InMemoryEventStore"/>.
    /// If <see cref="ILoggerFactory"/> is registered in the container,
    /// a logger is automatically resolved and injected into <see cref="ActorSystem"/>.
    /// </summary>
    public static SvcContainer AddPicoActor(this SvcContainer container)
    {
        return AddPicoActor(container, eventStore: null);
    }

    /// <summary>
    /// Registers PicoNode.Actor services with a custom event store.
    /// </summary>
    public static SvcContainer AddPicoActor(this SvcContainer container, IEventStore? eventStore)
    {
        // Register event store (custom or default in-memory)
        container.Register(
            typeof(IEventStore),
            scope => eventStore ?? new InMemoryEventStore(),
            SvcLifetime.Singleton
        );

        // Register ActorSystem singleton
        container.Register(
            typeof(IActorSystem),
            scope =>
            {
                var store = (IEventStore)scope.GetService(typeof(IEventStore));

                // Optional: resolve logger if PicoLog is registered in DI
                ILogger? logger = null;
                if (
                    scope.TryGetService(typeof(ILoggerFactory), out var factoryObj)
                    && factoryObj is ILoggerFactory loggerFactory
                )
                    logger = loggerFactory.CreateLogger(nameof(ActorSystem));

                return new ActorSystem(store, logger);
            },
            SvcLifetime.Singleton
        );

        return container;
    }
}
