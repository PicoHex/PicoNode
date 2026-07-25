using PicoJetson;
using PicoNode.Web;

namespace PicoWeb;

public sealed class JsonResult<T> : IWebResult
{
    private readonly T _value;
    private readonly int _statusCode;

    public JsonResult(T value, int statusCode = 200)
    {
        _value = value;
        _statusCode = statusCode;
    }

    public HttpResponse Execute(WebContext ctx)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_value);
        var resp = new HttpResponse
        {
            StatusCode = _statusCode,
            Body = bytes,
        };
        resp.Headers.Add("Content-Type", "application/json; charset=utf-8");
        return resp;
    }
}
