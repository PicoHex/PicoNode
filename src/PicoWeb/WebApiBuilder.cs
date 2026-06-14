namespace PicoWeb;

public sealed class WebApiBuilder
{
    private readonly SvcContainer _container = new();
    private WebAppOptions? _options;
    private JsonOptions? _jsonOptions;

    public WebApiBuilder ConfigureJson(Action<JsonOptions> configure)
    {
        _jsonOptions ??= new JsonOptions();
        configure(_jsonOptions);
        return this;
    }

    public WebApiBuilder ConfigureApp(Action<WebAppOptions> configure)
    {
        _options ??= new WebAppOptions();
        configure(_options);
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
        return new WebApiApp(app);
    }
}
