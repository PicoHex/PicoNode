using PicoNode.Web;

namespace PicoHtmx;

public sealed class HtmxResult : IWebResult
{
    private readonly string _html;
    private readonly int _statusCode;
    private readonly Dictionary<string, string> _htmxHeaders = new();

    public HtmxResult(string html, int statusCode = 200)
    {
        _html = html;
        _statusCode = statusCode;
    }

    public HtmxResult WithRedirect(string url)
    {
        _htmxHeaders["HX-Redirect"] = url;
        return this;
    }

    public HtmxResult WithTrigger(string jsonData)
    {
        _htmxHeaders["HX-Trigger"] = jsonData;
        return this;
    }

    public HtmxResult WithPushUrl(string url)
    {
        _htmxHeaders["HX-Push-Url"] = url;
        return this;
    }

    public HtmxResult WithRefresh()
    {
        _htmxHeaders["HX-Refresh"] = "true";
        return this;
    }

    public HtmxResult WithRetry(bool retry = true)
    {
        _htmxHeaders["HX-Retry"] = retry ? "true" : "false";
        return this;
    }

    public HtmxResult WithHeader(string name, string value)
    {
        _htmxHeaders[name] = value;
        return this;
    }

    public HttpResponse Execute(WebContext ctx)
    {
        var resp = new HttpResponse
        {
            StatusCode = _statusCode,
            Body = Encoding.UTF8.GetBytes(_html),
        };
        resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
        foreach (var (key, value) in _htmxHeaders)
            resp.Headers.Add(key, value);
        return resp;
    }
}
