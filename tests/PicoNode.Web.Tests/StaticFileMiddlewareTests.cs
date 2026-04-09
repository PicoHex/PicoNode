using System.IO;

namespace PicoNode.Web.Tests;

public sealed class StaticFileMiddlewareTests
{
    private string _tempDir = null!;

    [Before(Test)]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pico_static_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [After(Test)]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Serves_existing_html_file()
    {
        File.WriteAllText(Path.Combine(_tempDir, "page.html"), "<h1>Hello</h1>");
        var middleware = new StaticFileMiddleware(_tempDir);
        var context = CreateContext("GET", "/page.html");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("<h1>Hello</h1>");
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Content-Type", "text/html; charset=utf-8"));
    }

    [Test]
    public async Task Serves_index_html_for_root_path()
    {
        File.WriteAllText(Path.Combine(_tempDir, "index.html"), "home");
        var middleware = new StaticFileMiddleware(_tempDir);
        var context = CreateContext("GET", "/");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("home");
    }

    [Test]
    public async Task Returns_next_for_missing_file()
    {
        var middleware = new StaticFileMiddleware(_tempDir);
        var context = CreateContext("GET", "/missing.txt");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task Passes_through_for_non_get_methods()
    {
        File.WriteAllText(Path.Combine(_tempDir, "data.json"), "{}");
        var middleware = new StaticFileMiddleware(_tempDir);
        var context = CreateContext("POST", "/data.json");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task Prevents_path_traversal()
    {
        var parentFile = Path.Combine(
            Path.GetTempPath(),
            "secret_" + Guid.NewGuid().ToString("N") + ".txt"
        );
        try
        {
            File.WriteAllText(parentFile, "secret");
            var middleware = new StaticFileMiddleware(_tempDir);
            var context = CreateContext("GET", "/../" + Path.GetFileName(parentFile));

            var response = await middleware.InvokeAsync(
                context,
                NotFoundHandler,
                CancellationToken.None
            );

            await Assert.That(response.StatusCode).IsEqualTo(404);
        }
        finally
        {
            File.Delete(parentFile);
        }
    }

    [Test]
    public async Task Serves_file_with_prefix()
    {
        File.WriteAllText(Path.Combine(_tempDir, "style.css"), "body{}");
        var middleware = new StaticFileMiddleware(_tempDir, "/static");
        var context = CreateContext("GET", "/static/style.css");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Content-Type", "text/css; charset=utf-8"));
    }

    [Test]
    public async Task Passes_through_when_prefix_does_not_match()
    {
        File.WriteAllText(Path.Combine(_tempDir, "style.css"), "body{}");
        var middleware = new StaticFileMiddleware(_tempDir, "/static");
        var context = CreateContext("GET", "/other/style.css");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task Maps_correct_content_types()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app.js"), "console.log(1)");
        var middleware = new StaticFileMiddleware(_tempDir);
        var context = CreateContext("GET", "/app.js");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert
            .That(response.Headers)
            .Contains(
                new KeyValuePair<string, string>(
                    "Content-Type",
                    "application/javascript; charset=utf-8"
                )
            );
    }

    [Test]
    public void Constructor_rejects_missing_directory()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new StaticFileMiddleware(Path.Combine(_tempDir, "nonexistent"))
        );
    }

    private static WebContext CreateContext(string method, string target) =>
        WebContext.Create(new HttpRequest { Method = method, Target = target });

    private static ValueTask<HttpResponse> NotFoundHandler(
        WebContext context,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(WebResults.Empty(404, "Not Found"));
}
