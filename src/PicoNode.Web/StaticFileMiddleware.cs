namespace PicoNode.Web;

public sealed class StaticFileMiddleware
{
    private readonly string _rootPath;
    private readonly string _requestPathPrefix;
    private readonly string? _defaultDocument;

    public StaticFileMiddleware(string rootPath, string requestPathPrefix = "/")
        : this(rootPath, new StaticFileMiddlewareOptions { RequestPathPrefix = requestPathPrefix }) { }

    public StaticFileMiddleware(string rootPath, StaticFileMiddlewareOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);

        ValidateRequestPathPrefix(options.RequestPathPrefix);
        ValidateDefaultDocument(options.DefaultDocument);

        _rootPath = Path.GetFullPath(rootPath);
        _requestPathPrefix = options.RequestPathPrefix.TrimEnd('/');
        _defaultDocument = options.DefaultDocument;

        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException(
                $"Static file root directory not found: {_rootPath}"
            );
        }
    }

    public async ValueTask<HttpResponse> InvokeAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken
    )
    {
        if (
            !context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !context.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        )
        {
            return await next(context, cancellationToken);
        }

        var requestPath = context.Path;

        if (_requestPathPrefix.Length > 0)
        {
            if (!MatchesRequestPathPrefix(requestPath))
            {
                return await next(context, cancellationToken);
            }

            requestPath = requestPath[_requestPathPrefix.Length..];
        }

        requestPath = TryApplyDefaultDocument(requestPath);

        var relativePath = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));

        if (!IsUnderRoot(fullPath) || !File.Exists(fullPath))
        {
            return await next(context, cancellationToken);
        }

        var contentType = ContentTypeMap.GetContentType(Path.GetExtension(fullPath));
        var fileInfo = new FileInfo(fullPath);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("Content-Type", contentType),
            new("Content-Length", fileInfo.Length.ToString()),
        };

        var isHead = context.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        return new HttpResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = headers,
            Body = ReadOnlyMemory<byte>.Empty,
            BodyStream = isHead
                ? null
                : new FileStream(
                    fullPath,
                    new FileStreamOptions
                    {
                        Access = FileAccess.Read,
                        Mode = FileMode.Open,
                        Share = FileShare.Read,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    }
                ),
        };
    }

    private bool MatchesRequestPathPrefix(string requestPath)
    {
        if (!requestPath.StartsWith(_requestPathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return requestPath.Length == _requestPathPrefix.Length
            || requestPath[_requestPathPrefix.Length] == '/';
    }

    private string TryApplyDefaultDocument(string requestPath)
    {
        if (_defaultDocument is null)
        {
            return requestPath;
        }

        if (requestPath.Length == 0 || requestPath == "/")
        {
            return "/" + _defaultDocument;
        }

        if (requestPath[^1] == '/')
        {
            return requestPath + _defaultDocument;
        }

        return requestPath;
    }

    private static void ValidateRequestPathPrefix(string requestPathPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPathPrefix);
        if (requestPathPrefix[0] != '/')
        {
            throw new ArgumentException("RequestPathPrefix must start with '/'.", nameof(requestPathPrefix));
        }
    }

    private static void ValidateDefaultDocument(string? defaultDocument)
    {
        if (defaultDocument is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDocument);
        if (
            defaultDocument.Contains('/')
            || defaultDocument.Contains('\\')
            || Path.IsPathRooted(defaultDocument)
            || defaultDocument is "." or ".."
        )
        {
            throw new ArgumentException(
                "DefaultDocument must be a single relative file name.",
                nameof(defaultDocument)
            );
        }
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (!fullPath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fullPath.Length == _rootPath.Length
            || fullPath[_rootPath.Length] == Path.DirectorySeparatorChar
            || fullPath[_rootPath.Length] == Path.AltDirectorySeparatorChar;
    }
}
