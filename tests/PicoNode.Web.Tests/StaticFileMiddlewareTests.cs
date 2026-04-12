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
        await Assert.That(response.Body.IsEmpty).IsTrue();
        await Assert.That(response.BodyStream).IsNotNull();
        await using var bodyStream = response.BodyStream!;
        using var reader = new StreamReader(bodyStream, Encoding.UTF8, leaveOpen: false);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("<h1>Hello</h1>");
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
        await Assert.That(response.Body.IsEmpty).IsTrue();
        await Assert.That(response.BodyStream).IsNotNull();
        await using var bodyStream = response.BodyStream!;
        using var reader = new StreamReader(bodyStream, Encoding.UTF8, leaveOpen: false);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("home");
    }

    [Test]
    public async Task Head_returns_content_length_without_body_stream()
    {
        File.WriteAllText(Path.Combine(_tempDir, "page.html"), "<h1>Hello</h1>");
        var middleware = new StaticFileMiddleware(_tempDir);
        var context = CreateContext("HEAD", "/page.html");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Body.IsEmpty).IsTrue();
        await Assert.That(response.BodyStream).IsNull();
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Content-Length", "14"));
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
    public async Task Prevents_sibling_root_prefix_bypass()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "pico_static_root_" + Guid.NewGuid().ToString("N"));
        var siblingDir = baseDir + "2";
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(siblingDir);
        File.WriteAllText(Path.Combine(siblingDir, "secret.txt"), "secret");

        try
        {
            var middleware = new StaticFileMiddleware(baseDir);
            var context = CreateContext("GET", "/../" + Path.GetFileName(siblingDir) + "/secret.txt");

            var response = await middleware.InvokeAsync(
                context,
                NotFoundHandler,
                CancellationToken.None
            );

            await Assert.That(response.StatusCode).IsEqualTo(404);
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }

            if (Directory.Exists(siblingDir))
            {
                Directory.Delete(siblingDir, recursive: true);
            }
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
        await Assert.That(response.BodyStream).IsNotNull();
        await using (response.BodyStream!) { }
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
    public async Task Passes_through_when_request_only_shares_prefix_text()
    {
        File.WriteAllText(Path.Combine(_tempDir, "style.css"), "body{}");
        var middleware = new StaticFileMiddleware(_tempDir, "/static");
        var context = CreateContext("GET", "/staticity/style.css");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task Prefix_exact_match_still_resolves_index_html()
    {
        File.WriteAllText(Path.Combine(_tempDir, "index.html"), "prefix-home");
        var middleware = new StaticFileMiddleware(_tempDir, "/static");
        var context = CreateContext("GET", "/static");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.BodyStream).IsNotNull();
        await using var bodyStream = response.BodyStream!;
        using var reader = new StreamReader(bodyStream, Encoding.UTF8, leaveOpen: false);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("prefix-home");
    }

    [Test]
    public async Task Serves_custom_default_document_for_root_path()
    {
        File.WriteAllText(Path.Combine(_tempDir, "home.html"), "custom-home");
        var middleware = new StaticFileMiddleware(
            _tempDir,
            new StaticFileMiddlewareOptions { DefaultDocument = "home.html" }
        );
        var context = CreateContext("GET", "/");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await using var bodyStream = response.BodyStream!;
        using var reader = new StreamReader(bodyStream, Encoding.UTF8, leaveOpen: false);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("custom-home");
    }

    [Test]
    public async Task Serves_custom_default_document_for_directory_path()
    {
        var docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "home.html"), "docs-home");
        var middleware = new StaticFileMiddleware(
            _tempDir,
            new StaticFileMiddlewareOptions { DefaultDocument = "home.html" }
        );
        var context = CreateContext("GET", "/docs/");

        var response = await middleware.InvokeAsync(
            context,
            NotFoundHandler,
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await using var bodyStream = response.BodyStream!;
        using var reader = new StreamReader(bodyStream, Encoding.UTF8, leaveOpen: false);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("docs-home");
    }

    [Test]
    public async Task Passes_through_when_default_document_is_disabled()
    {
        File.WriteAllText(Path.Combine(_tempDir, "index.html"), "home");
        var middleware = new StaticFileMiddleware(
            _tempDir,
            new StaticFileMiddlewareOptions { DefaultDocument = null }
        );
        var context = CreateContext("GET", "/");

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

        await Assert.That(response.BodyStream).IsNotNull();
        await using (response.BodyStream!) { }
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

    [Test]
    public void Constructor_rejects_invalid_default_document()
    {
        Assert.Throws<ArgumentException>(
            () =>
                new StaticFileMiddleware(
                    _tempDir,
                    new StaticFileMiddlewareOptions { DefaultDocument = "docs/home.html" }
                )
        );
    }

    private static WebContext CreateContext(string method, string target) =>
        WebContext.Create(new HttpRequest { Method = method, Target = target });

    private static ValueTask<HttpResponse> NotFoundHandler(
        WebContext context,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(WebResults.Empty(404, "Not Found"));
}
