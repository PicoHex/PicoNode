using System.Text;
using PicoNode.Http;
using PicoNode.Web;

namespace PicoWeb.Samples;

public static class ShowcaseApp
{
    private const string ThemeCookieName = "pico-theme";
    private static readonly string[] SupportedThemes = ["light", "dark"];
    private static readonly CorsOptions CorsOptions = new()
    {
        AllowedOrigins = ["http://localhost:3000", "http://127.0.0.1:3000"],
        AllowedMethods = ["GET", "POST", "OPTIONS"],
        AllowedHeaders = ["Content-Type"],
        ExposedHeaders = ["Content-Encoding"],
        AllowCredentials = true,
        MaxAge = 600,
    };

    public static WebApp Create(string? contentRoot = null)
    {
        var staticRoot = Path.Combine(contentRoot ?? AppContext.BaseDirectory, "wwwroot");
        var app = new WebApp(
            new WebAppOptions
            {
                ServerHeader = "PicoWeb.Samples.Showcase",
                MaxRequestBytes = 256 * 1024,
            }
        );

        var compression = new CompressionMiddleware(minimumBodySize: 256);
        var staticFiles = new StaticFileMiddleware(staticRoot);

        app.Use((context, next, cancellationToken) => HandleCorsAsync(context, next, cancellationToken));
        app.Use(compression.InvokeAsync);
        app.Use(staticFiles.InvokeAsync);

        app.MapGet("/api/showcase", static (context, _) =>
        {
            var theme = GetTheme(context.Request);
            var json = BuildShowcasePayload(theme);
            return ValueTask.FromResult(WebResults.Json(200, json, "OK"));
        });

        app.MapGet("/api/preferences", static (context, _) =>
        {
            var theme = GetTheme(context.Request);
            var json = $$"""
            {
              "theme":"{{EscapeJson(theme)}}",
              "cookieName":"{{ThemeCookieName}}",
              "supportedThemes":["light","dark"]
            }
            """;

            return ValueTask.FromResult(WebResults.Json(200, json, "OK"));
        });

        app.MapPost("/api/preferences/{theme}", static (context, _) =>
        {
            if (!context.RouteValues.TryGetValue("theme", out var theme) || !IsSupportedTheme(theme))
            {
                return ValueTask.FromResult(
                    WebResults.Json(
                        400,
                        """
                        {
                          "error":"unsupported-theme",
                          "supportedThemes":["light","dark"]
                        }
                        """,
                        "Bad Request"
                    )
                );
            }

            theme = NormalizeTheme(theme);

            var response = WebResults.Json(
                200,
                $$"""
                {
                  "theme":"{{EscapeJson(theme)}}",
                  "stored":true
                }
                """,
                "OK"
            );

            var cookieHeader = new SetCookieBuilder(ThemeCookieName, theme)
                .Path("/")
                .MaxAge(60 * 60 * 24 * 30)
                .SameSite("Lax")
                .Build();

            return ValueTask.FromResult(WithHeaders(response, [cookieHeader]));
        });

        app.MapGet("/api/content", static (_, _) =>
        {
            var body = BuildCompressionPayload();
            return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
        });

        app.MapPost("/api/uploads", static (context, _) =>
        {
            var multipart = MultipartFormDataParser.Parse(context.Request);
            if (multipart is null)
            {
                return ValueTask.FromResult(
                    WebResults.Json(
                        415,
                        """
                        {
                          "error":"expected-multipart-form-data"
                        }
                        """,
                        "Unsupported Media Type"
                    )
                );
            }

            var json = BuildUploadPayload(multipart);
            return ValueTask.FromResult(WebResults.Json(200, json, "OK"));
        });

        app.MapFallback(static (context, _) =>
            ValueTask.FromResult(
                WebResults.Text(404, $"No route matched '{context.Path}'.", "Not Found")
            )
        );

        return app;
    }

    private static async ValueTask<HttpResponse> HandleCorsAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken
    )
    {
        if (!context.Path.StartsWith("/api", StringComparison.Ordinal))
        {
            return await next(context, cancellationToken);
        }

        var preflight = CorsHandler.HandlePreflight(context.Request, CorsOptions);
        if (preflight is not null)
        {
            return preflight;
        }

        var response = await next(context, cancellationToken);
        var corsHeaders = CorsHandler.GetResponseHeaders(context.Request, CorsOptions);
        return corsHeaders.Count == 0 ? response : WithHeaders(response, corsHeaders);
    }

    private static HttpResponse WithHeaders(
        HttpResponse response,
        IReadOnlyList<KeyValuePair<string, string>> extraHeaders
    )
    {
        var headers = new HttpHeaderCollection();
        foreach (var h in response.Headers)
            headers.Add(h);
        foreach (var h in extraHeaders)
            headers.Add(h);

        return new HttpResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version,
            Headers = headers,
            Body = response.Body,
            BodyStream = response.BodyStream,
        };
    }

    private static string GetTheme(HttpRequest request)
    {
        var cookies = CookieParser.Parse(request.HeaderFields);
        return cookies.TryGetValue(ThemeCookieName, out var theme) && IsSupportedTheme(theme)
            ? NormalizeTheme(theme)
            : "light";
    }

    private static bool IsSupportedTheme(string theme) => SupportedThemes.Contains(theme, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeTheme(string theme) => theme.ToLowerInvariant();

    private static string BuildShowcasePayload(string theme)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"name\":\"PicoWeb Static Showcase\",");
        builder.AppendLine("  \"theme\":\"" + EscapeJson(theme) + "\",");
        builder.AppendLine("  \"features\":[\"static-files\",\"compression\",\"cors\",\"cookies\",\"multipart-form-data\"],");
        builder.AppendLine("  \"cors\":{");
        builder.AppendLine("    \"allowedOrigins\":[\"http://localhost:3000\",\"http://127.0.0.1:3000\"],");
        builder.AppendLine("    \"allowCredentials\":true,");
        builder.AppendLine("    \"exposedHeaders\":[\"Content-Encoding\"]");
        builder.AppendLine("  },");
        builder.AppendLine("  \"endpoints\":{");
        builder.AppendLine("    \"showcase\":\"/api/showcase\",");
        builder.AppendLine("    \"preferences\":\"/api/preferences\",");
        builder.AppendLine("    \"content\":\"/api/content\",");
        builder.AppendLine("    \"uploads\":\"/api/uploads\"");
        builder.AppendLine("  }");
        builder.Append('}');
        return builder.ToString();
    }

    private static string BuildCompressionPayload()
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("PicoWeb compression showcase");
        builder.AppendLine("This endpoint intentionally returns a larger payload so response compression is easy to observe.");
        builder.AppendLine();

        for (var index = 1; index <= 24; index++)
        {
            builder.Append("Block ")
                .Append(index.ToString("00"))
                .Append(": static assets, cookie-backed preferences, multipart parsing, and CORS preflight all run through the same lightweight PicoWeb pipeline.")
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildUploadPayload(MultipartFormData multipart)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"fieldCount\":" + multipart.Fields.Count + ',');
        builder.AppendLine("  \"fileCount\":" + multipart.Files.Count + ',');
        builder.AppendLine("  \"fields\":[");

        for (var index = 0; index < multipart.Fields.Count; index++)
        {
            var field = multipart.Fields[index];
            builder.Append("    {\"name\":\"")
                .Append(EscapeJson(field.Name))
                .Append("\",\"value\":\"")
                .Append(EscapeJson(field.Value))
                .Append("\"}");

            if (index < multipart.Fields.Count - 1)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.AppendLine("  ],");
        builder.AppendLine("  \"files\":[");

        for (var index = 0; index < multipart.Files.Count; index++)
        {
            var file = multipart.Files[index];
            builder.Append("    {\"name\":\"")
                .Append(EscapeJson(file.Name))
                .Append("\",\"fileName\":\"")
                .Append(EscapeJson(file.FileName))
                .Append("\",\"contentType\":\"")
                .Append(EscapeJson(file.ContentType))
                .Append("\",\"length\":")
                .Append(file.Content.Length)
                .Append('}');

            if (index < multipart.Files.Count - 1)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.AppendLine("  ]");
        builder.Append('}');
        return builder.ToString();
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
