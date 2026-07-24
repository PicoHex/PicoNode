namespace PicoNode.Web.Tests;

public sealed class ExceptionHandlerMiddlewareTests
{
    private static WebContext CreateContext(string method, string path)
    {
        return WebContext.Create(
            new HttpRequest
            {
                Method = method,
                Target = path,
                HeaderFields = [],
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            }
        );
    }

    [Test]
    public async Task No_exception_passes_response_through_unchanged()
    {
        var middleware = ExceptionHandlerMiddleware.CreateDefault();
        var context = CreateContext("GET", "/api/test");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Route_handler_throws_returns_default_json_500()
    {
        var middleware = ExceptionHandlerMiddleware.CreateDefault();
        var context = CreateContext("GET", "/api/boom");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) => throw new InvalidOperationException("test exception"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(500);
        await Assert.That(response.ReasonPhrase).IsEqualTo("Internal Server Error");

        var body = Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(body).Contains("Internal Server Error");
        await Assert.That(body).Contains("traceId");

        await Assert
            .That(response.Headers.TryGetValue("Content-Type", out var ct))
            .IsTrue();
        await Assert.That(ct).Contains("application/json");
    }

    [Test]
    public async Task Custom_handler_receives_context_and_exception_and_returns_its_response()
    {
        WebContext? capturedContext = null;
        Exception? capturedException = null;

        var middleware = new ExceptionHandlerMiddleware(new ExceptionHandlerOptions
        {
            ExceptionHandler = (ctx, ex) =>
            {
                capturedContext = ctx;
                capturedException = ex;
                return WebResults.Json(422,
                    """{"error":"Validation failed"}""", "Unprocessable Entity");
            },
        });

        var context = CreateContext("POST", "/api/data");
        var thrown = new ArgumentException("bad input");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) => throw thrown,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(422);
        await Assert.That(response.ReasonPhrase).IsEqualTo("Unprocessable Entity");
        await Assert.That(capturedContext).IsSameReferenceAs(context);
        await Assert.That(capturedException).IsSameReferenceAs(thrown);
    }

    [Test]
    public async Task Custom_handler_throws_returns_bare_500()
    {
        var middleware = new ExceptionHandlerMiddleware(new ExceptionHandlerOptions
        {
            ExceptionHandler = (_, _) => throw new NullReferenceException("handler bug"),
        });

        var context = CreateContext("GET", "/api/crash");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) => throw new InvalidOperationException("original error"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(500);
        await Assert.That(response.ReasonPhrase).IsEqualTo("Internal Server Error");
        await Assert.That(response.Body.Length).IsEqualTo(0);
    }

    [Test]
    public async Task OperationCanceledException_on_cancellation_is_rethrown_not_caught()
    {
        var middleware = ExceptionHandlerMiddleware.CreateDefault();
        var context = CreateContext("GET", "/api/data");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () =>
            {
                await middleware.InvokeAsync(
                    context,
                    (_, _) => throw new OperationCanceledException(),
                    cts.Token
                );
            });
    }

    [Test]
    public async Task OperationCanceledException_not_from_token_is_caught_normally()
    {
        var middleware = ExceptionHandlerMiddleware.CreateDefault();
        var context = CreateContext("GET", "/api/data");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) => throw new OperationCanceledException("not a real cancellation"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(500);
    }

    [Test]
    public async Task Logger_receives_exception_with_method_and_path()
    {
        var logger = new TestLogger();

        var middleware = new ExceptionHandlerMiddleware(new ExceptionHandlerOptions
        {
            Logger = logger,
            ExceptionHandler = (_, _) =>
                WebResults.Json(500, """{"error":"boom"}""", "Internal Server Error"),
        });

        var context = CreateContext("DELETE", "/api/resource/42");

        _ = await middleware.InvokeAsync(
            context,
            static (_, _) => throw new InvalidOperationException("kaboom"),
            CancellationToken.None
        );

        await Assert.That(logger.LastLogLevel).IsEqualTo(LogLevel.Error);
        await Assert.That(logger.LastMessage).Contains("DELETE");
        await Assert.That(logger.LastMessage).Contains("/api/resource/42");
        await Assert.That(logger.LastException).IsNotNull();
        await Assert
            .That(logger.LastException!.Message)
            .IsEqualTo("kaboom");
    }

    [Test]
    public async Task Logger_null_does_not_throw()
    {
        var middleware = new ExceptionHandlerMiddleware(new ExceptionHandlerOptions
        {
            Logger = null,
            ExceptionHandler = (_, _) =>
                WebResults.Json(500, """{"error":"ok"}""", "Internal Server Error"),
        });

        var context = CreateContext("GET", "/api/test");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) => throw new InvalidOperationException("test"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(500);
    }

    [Test]
    public async Task Response_has_no_connection_close_header()
    {
        var middleware = ExceptionHandlerMiddleware.CreateDefault();
        var context = CreateContext("GET", "/api/boom");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) => throw new InvalidOperationException("boom"),
            CancellationToken.None
        );

        await Assert
            .That(response.Headers.TryGetValue("Connection", out _))
            .IsFalse();
    }

    private sealed class TestLogger : ILogger
    {
        public LogLevel? LastLogLevel { get; private set; }
        public string? LastMessage { get; private set; }
        public Exception? LastException { get; private set; }

        public void Log(
            LogLevel level,
            EventId eventId,
            string message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception)
        {
            LastLogLevel = level;
            LastMessage = message;
            LastException = exception;
        }

        public void Log(
            LogLevel level,
            EventId eventId,
            FormattableString message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception) =>
            Log(level, eventId, message.Format, args, exception);

        public void Log(
            LogLevel level,
            string message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception) =>
            Log(level, new EventId(0), message, args, exception);

        public void Log(
            LogLevel level,
            FormattableString message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception) =>
            Log(level, new EventId(0), message.Format, args, exception);

        public Task LogAsync(
            LogLevel level,
            EventId eventId,
            string message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception,
            CancellationToken cancellationToken)
        {
            Log(level, eventId, message, args, exception);
            return Task.CompletedTask;
        }

        public Task LogAsync(
            LogLevel level,
            EventId eventId,
            FormattableString message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception,
            CancellationToken cancellationToken)
        {
            Log(level, eventId, message.Format, args, exception);
            return Task.CompletedTask;
        }

        public Task LogAsync(
            LogLevel level,
            string message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception,
            CancellationToken cancellationToken)
        {
            Log(level, new EventId(0), message, args, exception);
            return Task.CompletedTask;
        }

        public Task LogAsync(
            LogLevel level,
            FormattableString message,
            IReadOnlyList<KeyValuePair<string, object?>>? args,
            Exception? exception,
            CancellationToken cancellationToken)
        {
            Log(level, new EventId(0), message.Format, args, exception);
            return Task.CompletedTask;
        }

        public bool IsEnabled(LogLevel level) => true;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
