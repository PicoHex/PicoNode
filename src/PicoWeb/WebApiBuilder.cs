namespace PicoWeb;

public sealed class WebApiBuilder
{
    private readonly SvcContainer _container = new();
    private WebAppOptions _options = new();
    private JsonOptions? _jsonOptions;

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

    public WebApiBuilder RegisterSingleton<TService, TImpl>()
        where TImpl : TService
    {
        _container.RegisterSingleton(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiBuilder RegisterScoped<TService, TImpl>()
        where TImpl : TService
    {
        _container.RegisterScoped(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiBuilder RegisterTransient<TService, TImpl>()
        where TImpl : TService
    {
        _container.RegisterTransient(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiApp Build()
    {
        _container.Build();
        if (_jsonOptions is not null)
            AppSerializationOptions.Default = _jsonOptions;

        var app = new WebApp(_container, _options ?? new());

        // Auto-register controller endpoints if EndpointRegistrar exists
        TryRegisterEndpoints(app);

        return new WebApiApp(app);
    }

    private static void TryRegisterEndpoints(WebApp app)
    {
#pragma warning disable IL2026, IL2075 // Reflection fallback for development
        // Search all loaded assemblies for the generated EndpointRegistrar
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var registrarType = asm.GetType("EndpointRegistrar");
            if (registrarType is null)
                continue;

            var method = registrarType.GetMethod("RegisterAll",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method is null)
                continue;

            method.Invoke(null, [app]);
            return;
        }
#pragma warning restore IL2026, IL2075
    }
}
