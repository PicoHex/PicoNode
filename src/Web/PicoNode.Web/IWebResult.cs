namespace PicoNode.Web;

public interface IWebResult
{
    HttpResponse Execute(WebContext ctx);
}

public sealed class EmptyResult : IWebResult
{
    private readonly int _statusCode;
    public EmptyResult(int statusCode) => _statusCode = statusCode;
    public HttpResponse Execute(WebContext ctx) =>
        new() { StatusCode = _statusCode };
}

public sealed class HtmlResult : IWebResult
{
    private readonly string _html;
    private readonly int _statusCode;
    public HtmlResult(string html, int statusCode = 200)
    {
        _html = html;
        _statusCode = statusCode;
    }
    public HttpResponse Execute(WebContext ctx)
    {
        var resp = new HttpResponse
        {
            StatusCode = _statusCode,
            Body = Encoding.UTF8.GetBytes(_html),
        };
        resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
        return resp;
    }
}

public sealed class RedirectResult : IWebResult
{
    private readonly string _location;
    private readonly bool _permanent;
    public RedirectResult(string location, bool permanent = false)
    {
        _location = location;
        _permanent = permanent;
    }
    public HttpResponse Execute(WebContext ctx)
    {
        var resp = new HttpResponse
        {
            StatusCode = _permanent ? 301 : 302,
            ReasonPhrase = _permanent ? "Moved Permanently" : "Found",
        };
        resp.Headers.Add("Location", _location);
        return resp;
    }
}

public sealed class TextResult : IWebResult
{
    private readonly string _text;
    private readonly int _statusCode;
    public TextResult(string text, int statusCode = 200)
    {
        _text = text;
        _statusCode = statusCode;
    }
    public HttpResponse Execute(WebContext ctx)
    {
        var resp = new HttpResponse
        {
            StatusCode = _statusCode,
            Body = Encoding.UTF8.GetBytes(_text),
        };
        resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        return resp;
    }
}
