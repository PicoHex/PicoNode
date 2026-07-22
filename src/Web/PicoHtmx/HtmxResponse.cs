namespace PicoHtmx;

public static class Htmx
{
    public static HttpResponse Html(string body, int code = 200)
    {
        var resp = new HttpResponse
        {
            StatusCode = code,
            Body = Encoding.UTF8.GetBytes(body)
        };
        resp.Headers.Add("Content-Type", "text/html; charset=utf-8");
        return resp;
    }

    public static HttpResponse SseHtml(string html)
    {
        var resp = new HttpResponse
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes($"data: {html}\n\n")
        };
        resp.Headers.Add("Content-Type", "text/event-stream");
        resp.Headers.Add("Cache-Control", "no-cache");
        return resp;
    }

    public static HttpResponse Redirect(string url)
    {
        var resp = Html("", 200);
        resp.Headers.Add("HX-Redirect", url);
        return resp;
    }

    public static HttpResponse Refresh()
    {
        var resp = Html("", 200);
        resp.Headers.Add("HX-Refresh", "true");
        return resp;
    }

    public static HttpResponse Trigger(string eventName, string data = "")
    {
        var resp = Html("", 200);
        resp.Headers.Add("HX-Trigger", data);
        return resp;
    }

    public static HttpResponse Ok()
    {
        return Html("ok", 200);
    }
}
