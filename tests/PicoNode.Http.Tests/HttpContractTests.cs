namespace PicoNode.Http.Tests;

public sealed class HttpContractTests
{
    [Test]
    public async Task HttpConnectionHandlerOptions_exposes_expected_contract_shape()
    {
        var type = typeof(HttpConnectionHandlerOptions);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(
                type.GetProperty(nameof(HttpConnectionHandlerOptions.RequestHandler))?.PropertyType
            )
            .IsEqualTo(typeof(HttpRequestHandler));
        await Assert
            .That(type.GetProperty(nameof(HttpConnectionHandlerOptions.ServerHeader))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(
                type.GetProperty(nameof(HttpConnectionHandlerOptions.MaxRequestBytes))?.PropertyType
            )
            .IsEqualTo(typeof(int));
        await Assert
            .That(properties)
            .IsEquivalentTo(

                [
                    nameof(HttpConnectionHandlerOptions.MaxRequestBytes),
                    nameof(HttpConnectionHandlerOptions.RequestHandler),
                    nameof(HttpConnectionHandlerOptions.ServerHeader),
                ]
            );
    }

    [Test]
    public async Task HttpRequestHandler_exposes_expected_contract_shape()
    {
        var invoke = typeof(HttpRequestHandler).GetMethod(
            "Invoke",
            BindingFlags.Public | BindingFlags.Instance
        );

        await Assert.That(typeof(HttpRequestHandler).BaseType).IsEqualTo(typeof(MulticastDelegate));
        await Assert.That(invoke?.ReturnType).IsEqualTo(typeof(ValueTask<HttpResponse>));
        await Assert
            .That(invoke?.GetParameters().Select(x => x.ParameterType).ToArray())
            .IsEquivalentTo([typeof(HttpRequest), typeof(CancellationToken)]);
    }

    [Test]
    public async Task HttpRequest_exposes_expected_contract_shape()
    {
        var type = typeof(HttpRequest);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(x => x.Name)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties.Select(x => x.Name).ToArray())
            .IsEquivalentTo(

                [
                    nameof(HttpRequest.Body),
                    nameof(HttpRequest.HeaderFields),
                    nameof(HttpRequest.Headers),
                    nameof(HttpRequest.Method),
                    nameof(HttpRequest.Target),
                    nameof(HttpRequest.Version),
                ]
            );

        await Assert.That(type.GetMethod(nameof(HttpRequest.CreateBodyStream))).IsNotNull();
        await Assert
            .That(type.GetProperty(nameof(HttpRequest.Method))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpRequest.Target))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpRequest.Version))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpRequest.HeaderFields))?.PropertyType)
            .IsEqualTo(typeof(IReadOnlyList<KeyValuePair<string, string>>));
        await Assert
            .That(type.GetProperty(nameof(HttpRequest.Headers))?.PropertyType)
            .IsEqualTo(typeof(IReadOnlyDictionary<string, string>));
        await Assert
            .That(type.GetProperty(nameof(HttpRequest.Body))?.PropertyType)
            .IsEqualTo(typeof(ReadOnlyMemory<byte>));
    }

    [Test]
    public async Task HttpResponse_exposes_expected_contract_shape()
    {
        var type = typeof(HttpResponse);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(x => x.Name)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties.Select(x => x.Name).ToArray())
            .IsEquivalentTo(

                [
                    nameof(HttpResponse.Body),
                    nameof(HttpResponse.BodyStream),
                    nameof(HttpResponse.Headers),
                    nameof(HttpResponse.ReasonPhrase),
                    nameof(HttpResponse.StatusCode),
                    nameof(HttpResponse.Version),
                ]
            );
        await Assert
            .That(type.GetProperty(nameof(HttpResponse.StatusCode))?.PropertyType)
            .IsEqualTo(typeof(int));
        await Assert
            .That(type.GetProperty(nameof(HttpResponse.ReasonPhrase))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpResponse.Version))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpResponse.Headers))?.PropertyType)
            .IsEqualTo(typeof(IReadOnlyList<KeyValuePair<string, string>>));
        await Assert
            .That(type.GetProperty(nameof(HttpResponse.Body))?.PropertyType)
            .IsEqualTo(typeof(ReadOnlyMemory<byte>));
    }

    [Test]
    public async Task HttpRoute_exposes_expected_contract_shape()
    {
        var type = typeof(HttpRoute);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(x => x.Name)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties.Select(x => x.Name).ToArray())
            .IsEquivalentTo(
                [nameof(HttpRoute.Handler), nameof(HttpRoute.Method), nameof(HttpRoute.Path),]
            );
        await Assert
            .That(type.GetProperty(nameof(HttpRoute.Method))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpRoute.Path))?.PropertyType)
            .IsEqualTo(typeof(string));
        await Assert
            .That(type.GetProperty(nameof(HttpRoute.Handler))?.PropertyType)
            .IsEqualTo(typeof(HttpRequestHandler));
    }

    [Test]
    public async Task HttpRouterOptions_exposes_expected_contract_shape()
    {
        var type = typeof(HttpRouterOptions);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(x => x.Name)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties.Select(x => x.Name).ToArray())
            .IsEquivalentTo(
                [nameof(HttpRouterOptions.FallbackHandler), nameof(HttpRouterOptions.Routes),]
            );
        await Assert
            .That(type.GetProperty(nameof(HttpRouterOptions.Routes))?.PropertyType)
            .IsEqualTo(typeof(IReadOnlyList<HttpRoute>));
        await Assert
            .That(type.GetProperty(nameof(HttpRouterOptions.FallbackHandler))?.PropertyType)
            .IsEqualTo(typeof(HttpRequestHandler));
    }

    [Test]
    public async Task HttpRouter_exposes_expected_contract_shape()
    {
        var type = typeof(HttpRouter);
        var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            )
            .Where(x => !x.IsSpecialName)
            .OrderBy(x => x.Name)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert.That(type.GetConstructors().Length).IsEqualTo(1);
        await Assert.That(type.GetConstructor([typeof(HttpRouterOptions)])).IsNotNull();
        await Assert
            .That(methods.Select(x => x.Name).ToArray())
            .IsEquivalentTo([nameof(HttpRouter.HandleAsync)]);
        await Assert
            .That(type.GetMethod(nameof(HttpRouter.HandleAsync))?.ReturnType)
            .IsEqualTo(typeof(ValueTask<HttpResponse>));
    }

    [Test]
    public async Task HttpConnectionHandler_exposes_expected_contract_shape()
    {
        var type = typeof(HttpConnectionHandler);
        var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            )
            .Where(x => !x.IsSpecialName)
            .OrderBy(x => x.Name)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert.That(type.GetInterfaces().Contains(typeof(ITcpConnectionHandler))).IsTrue();
        await Assert.That(type.GetConstructors().Length).IsEqualTo(1);
        await Assert.That(type.GetConstructor([typeof(HttpConnectionHandlerOptions)])).IsNotNull();
        await Assert
            .That(methods.Select(x => x.Name).ToArray())
            .IsEquivalentTo(

                [
                    nameof(HttpConnectionHandler.OnClosedAsync),
                    nameof(HttpConnectionHandler.OnConnectedAsync),
                    nameof(HttpConnectionHandler.OnReceivedAsync),
                ]
            );
        await Assert
            .That(type.GetMethod(nameof(HttpConnectionHandler.OnConnectedAsync))?.ReturnType)
            .IsEqualTo(typeof(Task));
        await Assert
            .That(type.GetMethod(nameof(HttpConnectionHandler.OnReceivedAsync))?.ReturnType)
            .IsEqualTo(typeof(ValueTask<SequencePosition>));
        await Assert
            .That(type.GetMethod(nameof(HttpConnectionHandler.OnClosedAsync))?.ReturnType)
            .IsEqualTo(typeof(Task));
    }
}
