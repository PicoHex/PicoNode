using PicoNode.Web;

namespace PicoWeb.Integration.Tests.Controllers;

// Discovered by Controllers.Gen via the Controllers/ folder convention:
//   class WidgetsController → prefix /api/widgets
//   GetWidget(int id)      → GET /api/widgets/widget/{id}
public class WidgetsController
{
    public HtmlResult GetItem(int id) => new HtmlResult($"<h1>item {id}</h1>");
}
