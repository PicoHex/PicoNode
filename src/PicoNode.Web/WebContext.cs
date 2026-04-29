namespace PicoNode.Web;

public sealed class WebContext
{
    private static readonly Dictionary<string, string> EmptyDictionary =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, string>? _query;
    private IReadOnlyDictionary<string, string> _routeValues = EmptyDictionary;
    private string? _pathString;

    internal WebContext(HttpRequest request, ReadOnlyMemory<char> path, string queryString)
    {
        Request = request;
        PathMemory = path;
        QueryString = queryString;
    }

    public HttpRequest Request { get; }

    /// <summary>
    /// Request path as a string. Computed lazily from the stored memory;
    /// routing hot paths should prefer <see cref="PathMemory"/>.<c>Span</c>
    /// to avoid the allocation.
    /// </summary>
    public string Path => _pathString ??= PathMemory.ToString();

    /// <summary>
    /// Request path as <see cref="ReadOnlyMemory{T}"/> without allocation.
    /// </summary>
    internal ReadOnlyMemory<char> PathMemory { get; }

    public string QueryString { get; }

    public IReadOnlyDictionary<string, string> RouteValues => _routeValues;

    public IReadOnlyDictionary<string, string> Query =>
        _query ??= QueryStringParser.Parse(QueryString);

    internal void SetRouteValues(Dictionary<string, string> values) => _routeValues = values;

    public PicoNode.Web.Abstractions.IServiceScope? Services { get; internal set; }

    public static WebContext Create(HttpRequest request)
    {
        var target = request.Target;
        var queryIndex = target.IndexOf('?');

        ReadOnlyMemory<char> path;
        string queryString;

        if (queryIndex >= 0)
        {
            path = target.AsMemory(0, queryIndex);
            queryString = target[(queryIndex + 1)..];
        }
        else
        {
            path = target.AsMemory();
            queryString = string.Empty;
        }

        return new WebContext(request, path, queryString);
    }
}
