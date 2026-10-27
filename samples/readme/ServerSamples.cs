// The README blocks that show the option object and the built-in server middleware, verbatim and
// compiled: `readme:options`, `readme:reload`, `readme:logging`, `readme:compression`,
// `readme:files`. Each region is mirrored into all ten READMEs and compared by
// tests/PicoNode.Docs.Tests; compiling it here is what makes the comparison worth something.
//
// Compiling these found two things the prose had been hiding: the logging sample called a
// `ConsoleSink` type that does not exist in PicoLog (`ColoredConsoleSink(ConsoleFormatter)` is the
// real console sink), and seven of the ten translations had dropped `Config = config` from the
// reload sample. Both are fixed in the READMEs, not worked around here.

using System.IO.Compression;
using System.Net;
using PicoCfg.Abs;
using PicoLog;
using PicoNode;
using PicoNode.Abs;
using PicoNode.Web;

internal static class ServerSamples
{
    /// <summary>Scaffolding the "Transport options" block uses but does not declare.</summary>
    public static void Options()
    {
        // #region readme:options
        var options = new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Any, 8080),
            MaxConnections = 500,
            IdleTimeout = TimeSpan.FromMinutes(5),
        };
        var node = new TcpNode(options);
        // #endregion
    }

    /// <summary>The "runtime config reload" block; <c>config</c> is the application's root.</summary>
    public static void Reload(ICfgRoot config)
    {
        // #region readme:reload
        // TcpNode supports runtime config reload (except Endpoint)
        var options = new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
            Config = config, // ICfgRoot for live reload
        };
        // Node starts a reload loop watching for config changes
        // #endregion
    }

    /// <summary>The "Logging" block; the handler is the application's.</summary>
    public static void Logging(ITcpConnectionHandler handler)
    {
        // #region readme:logging
        var logger = new LoggerFactory([new ColoredConsoleSink(new ConsoleFormatter())])
            .CreateLogger("PicoNode.Tcp");

        var node = new TcpNode(new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
            ConnectionHandler = handler,
            Logger = logger, // All transport faults logged here
        });

        // Log output:
        // [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
        // [Warning] Operation tcp.reject.limit failed: SessionRejected
        // [Debug] Socket shutdown during TLS teardown failed
        // #endregion
    }

    /// <summary>The "Compression" block.</summary>
    public static void Compression(WebApp app)
    {
        // #region readme:compression
        var compression = new CompressionMiddleware(
            CompressionLevel.Fastest, minimumBodySize: 860);
        app.Use(compression.InvokeAsync);
        // #endregion
    }

    /// <summary>The "Static Files" block.</summary>
    public static void StaticFiles(WebApp app)
    {
        // #region readme:files
        var staticFiles = new StaticFileMiddleware(
            "/path/to/wwwroot", requestPathPrefix: "/static");
        app.Use(staticFiles.InvokeAsync);
        // #endregion
    }

    /// <summary>The "CORS" block: one middleware wrapping the rest of the chain.</summary>
    public static void Cors(WebApp app)
    {
        // #region readme:cors
        app.Use(async (ctx, next, ct) =>
        {
            var corsOptions = new CorsOptions
            {
                AllowedOrigins = ["https://example.com"],
                AllowedMethods = ["GET", "POST"],
                AllowCredentials = true,
            };
            var preflight = CorsHandler.HandlePreflight(ctx.Request, corsOptions);
            if (preflight is not null)
                return preflight;
            var response = await next(ctx, ct);
            // Add CORS response headers
            foreach (var header in CorsHandler.GetResponseHeaders(ctx.Request, corsOptions))
            {
                response.Headers.Add(header.Key, header.Value);
            }
            return response;
        });
        // #endregion
    }

    /// <summary>The "Cookies &amp; Multipart" block.</summary>
    public static async ValueTask Cookies(WebContext context)
    {
        // #region readme:cookies
        // Cookie parsing
        var cookies = CookieParser.Parse(context.Request.HeaderFields);

        // Set-Cookie
        var setCookie = new SetCookieBuilder("session", "abc123")
            .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
            .Build();

        // Multipart form data
        // Limits (MaxPartSizeBytes/MaxTotalSizeBytes) are enforced on both the in-memory
        // Body path and the streaming BodyStream path; exceeding one throws InvalidDataException.
        var form = await MultipartFormDataParser.ParseAsync(
            context.Request,
            new MultipartFormDataParserOptions
            {
                MaxPartSizeBytes = 64 * 1024 * 1024, // per part (default)
                MaxTotalSizeBytes = 64 * 1024 * 1024, // all parts (default)
                MaxBoundaryLength = 70, // default
            }
        );
        foreach (var field in form?.Fields ?? [])
            Console.WriteLine($"{field.Name} = {field.Value}");
        foreach (var file in form?.Files ?? [])
            Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length} bytes)");
        // #endregion
    }
}
