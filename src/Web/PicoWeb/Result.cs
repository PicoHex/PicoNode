using PicoNode.Web;

namespace PicoWeb;

public static class Result
{
    public static IWebResult Ok<T>(T value) => new JsonResult<T>(value);
    public static IWebResult Created<T>(T value) => new JsonResult<T>(value, 201);
    public static IWebResult Html(string html, int statusCode = 200) =>
        new HtmlResult(html, statusCode);
    public static IWebResult Empty(int statusCode) =>
        new EmptyResult(statusCode);
    public static IWebResult Redirect(string location, bool permanent = false) =>
        new RedirectResult(location, permanent);
    public static IWebResult Text(string text, int statusCode = 200) =>
        new TextResult(text, statusCode);
}
