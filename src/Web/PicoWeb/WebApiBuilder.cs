namespace PicoWeb;

public sealed class WebApiBuilder
{
    private readonly ISvcContainer _container;
    private readonly SvcContainer? _ownedContainer;
    private WebAppOptions _options = new();
    private JsonOptions? _jsonOptions;

    /// <summary>Creates a builder with a default DI container.</summary>
    public WebApiBuilder()
    {
        _ownedContainer = new SvcContainer();
        _container = _ownedContainer;
    }

    /// <summary>Creates a builder with a pre-configured DI container for advanced scenarios.</summary>
    public WebApiBuilder(ISvcContainer container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    public WebApiBuilder ConfigureJson(Action<JsonOptions> configure)
    {
        _jsonOptions ??= new JsonOptions();
        configure(_jsonOptions);
        return this;
    }

    public WebApiBuilder ConfigureApp(Func<WebAppOptions, WebAppOptions> configure)
    {
        _options = configure(new WebAppOptions());
        return this;
    }

    public WebApiBuilder RegisterSingleton<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] TService,
        TImpl
    >()
        where TImpl : TService
    {
        if (_ownedContainer is null)
            throw new InvalidOperationException(
                "Cannot register services on an externally-provided DI container. "
                    + "Use the default constructor or register services directly on your container."
            );
        _ownedContainer.RegisterSingleton(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiBuilder RegisterScoped<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] TService,
        TImpl
    >()
        where TImpl : TService
    {
        if (_ownedContainer is null)
            throw new InvalidOperationException(
                "Cannot register services on an externally-provided DI container."
            );
        _ownedContainer.RegisterScoped(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiBuilder RegisterTransient<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] TService,
        TImpl
    >()
        where TImpl : TService
    {
        if (_ownedContainer is null)
            throw new InvalidOperationException(
                "Cannot register services on an externally-provided DI container."
            );
        _ownedContainer.RegisterTransient(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiApp Build()
    {
        _ownedContainer?.Build();
        if (_jsonOptions is not null)
            AppSerializationOptions.Default = _jsonOptions;

        var app = new WebApp(_container, _options ?? new());
        return new WebApiApp(app);
    }
}
