using PicoNode.Web;

namespace PicoWeb.Samples.Controllers;

[ApiController]  // Works outside Controllers/ folder too
public class HtmlController
{
    public HtmlResult GetPage() =>
        new HtmlResult("<h1>Hello from Controller!</h1>");

    public HtmlResult GetItem(int id) =>
        new HtmlResult($"<p>Item {id}</p>");
}
