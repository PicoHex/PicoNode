using PicoNode.Web;

namespace PicoWeb.Samples.Controllers;

/// <summary>
/// Enables convention-based controller discovery outside the Controllers/ folder.
/// The Controllers.Gen source generator checks for this attribute by name.
/// </summary>
public class ApiControllerAttribute : Attribute;

[ApiController]  // Works outside Controllers/ folder too
public class HtmlController
{
    public HtmlResult GetPage() =>
        new HtmlResult("<h1>Hello from Controller!</h1>");

    public HtmlResult GetItem(int id) =>
        new HtmlResult($"<p>Item {id}</p>");
}
