namespace PicoNode.Web;

public sealed class StaticFileMiddleware
{
    private readonly string _rootPath;
    private readonly string _requestPathPrefix;

    public StaticFileMiddleware(string rootPath, string requestPathPrefix = "/")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = Path.GetFullPath(rootPath);
        _requestPathPrefix = requestPathPrefix.TrimEnd('/');

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

        if (requestPath.Length == 0 || requestPath == "/")
        {
            requestPath = "/index.html";
        }

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
