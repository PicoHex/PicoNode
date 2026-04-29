namespace PicoNode.Web;

public sealed class WebContext
{
    private static readonly Dictionary<string, string> EmptyDictionary =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, string>? _query;
    private IReadOnlyDictionary<string, string> _routeValues = EmptyDictionary;

    internal WebContext(HttpRequest request, string path, string queryString)
    {
        Request = request;
        Path = path;
        QueryString = queryString;
    }

    public HttpRequest Request { get; }

    public string Path { get; }

    public string QueryString { get; }

    public IReadOnlyDictionary<string, string> RouteValues => _routeValues;

    public IReadOnlyDictionary<string, string> Query =>
        _query ??= QueryStringParser.Parse(QueryString);

    internal void SetRouteValues(Dictionary<string, string> values) => _routeValues = values;

    public PicoNode.Web.Abstractions.IServiceScope? Services { get; internal set; }

    public static WebContext Create(HttpRequest request)
    {
        var target = request.Target.AsSpan();
        var queryIndex = target.IndexOf('?');

        var path = queryIndex >= 0 ? target[..queryIndex].ToString() : request.Target;
        var queryString = queryIndex >= 0 ? target[(queryIndex + 1)..].ToString() : string.Empty;

        return new WebContext(request, path, queryString);
    }
}
