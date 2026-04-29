using System.IO.Compression;
using System.Net.Http.Headers;
using PicoWeb;
using PicoWeb.Samples;

namespace PicoNode.Smoke;

public sealed class PicoWebShowcaseSmokeTests
{
    [Test]
    public async Task Serves_static_landing_page()
    {
        await using var host = await StartHostAsync();

        var response = await host.Client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        await Assert.That(body).Contains("PicoWeb Static Showcase");
    }

    [Test]
    public async Task Returns_cookie_backed_preference_state()
    {
        await using var host = await StartHostAsync(useCookies: true);

        var setResponse = await host.Client.PostAsync("/api/preferences/dark", content: null);
        var getResponse = await host.Client.GetAsync("/api/preferences");
        var body = await getResponse.Content.ReadAsStringAsync();

        await Assert.That(setResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert
            .That(setResponse.Headers.TryGetValues("Set-Cookie", out var cookieHeaders))
            .IsTrue();
        await Assert.That(cookieHeaders!.First()).Contains("pico-theme=dark");
        await Assert.That(body).Contains("\"theme\":\"dark\"");
    }

    [Test]
    public async Task Parses_multipart_form_data_uploads()
    {
        await using var host = await StartHostAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("sample upload"), "metadata");
        content.Add(new ByteArrayContent("hello showcase"u8.ToArray()), "file", "hello.txt");

        var response = await host.Client.PostAsync("/api/uploads", content);
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).Contains("\"fieldCount\":1");
        await Assert.That(body).Contains("\"fileCount\":1");
        await Assert.That(body).Contains("hello.txt");
    }

    [Test]
    public async Task Handles_cors_preflight_requests()
    {
        await using var host = await StartHostAsync();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/uploads");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        var response = await host.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert
            .That(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins))
            .IsTrue();
        await Assert.That(origins!.Single()).IsEqualTo("http://localhost:3000");
    }

    [Test]
    public async Task Compresses_large_content_when_requested()
    {
        await using var host = await StartHostAsync(
            automaticDecompression: DecompressionMethods.None
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/content");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var response = await host.Client.SendAsync(request);
        var compressedBytes = await response.Content.ReadAsByteArrayAsync();
        var decompressed = await DecompressGzipAsync(compressedBytes);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentEncoding).Contains("gzip");
        await Assert.That(decompressed).Contains("PicoWeb compression showcase");
    }

    private static async Task<ShowcaseHost> StartHostAsync(
        bool useCookies = false,
        DecompressionMethods automaticDecompression = DecompressionMethods.None
    )
    {
        var port = GetAvailablePort();
        var sampleRoot = Path.Combine(GetRepositoryRoot(), "samples", "PicoWeb.Samples");
        var server = new WebServer(
            ShowcaseApp.Create(sampleRoot),
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) },
            new EmptyServiceProvider()
        );

        await server.StartAsync();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = automaticDecompression,
            UseCookies = useCookies,
        };

        if (useCookies)
        {
            handler.CookieContainer = new CookieContainer();
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute),
        };

        return new ShowcaseHost(server, client);
    }

    private static async Task<string> DecompressGzipAsync(byte[] compressedBytes)
    {
        await using var input = new MemoryStream(compressedBytes);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return await reader.ReadToEndAsync();
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
        );
    }

    private sealed class ShowcaseHost(WebServer server, HttpClient client) : IAsyncDisposable
    {
        public WebServer Server { get; } = server;

        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }
}

internal sealed class EmptyServiceProvider : PicoNode.Web.Abstractions.IServiceProvider
{
    public PicoNode.Web.Abstractions.IServiceScope CreateScope() => new EmptyServiceScope();
}

internal sealed class EmptyServiceScope : PicoNode.Web.Abstractions.IServiceScope
{
    public object? GetService(Type serviceType) => null;

    public void Dispose() { }
}
