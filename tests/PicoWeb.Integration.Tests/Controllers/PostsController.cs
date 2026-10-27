using PicoNode.Web;

namespace PicoWeb.Integration.Tests.Controllers;

// Mirrors samples/PicoWeb.Samples/Controllers/PostsController.cs (audit F-02):
//   [HttpPost] with no path argument → route is the bare prefix /api/posts,
//   so the simple-typed parameter `title` has NO route segment to bind from.
// The generated handler must read it from the query string and answer 400 when
// it is missing — never an unguarded RouteValues indexer (500, KeyNotFoundException).
public class PostsController
{
    [HttpPost]
    public HtmlResult Create(string title) => new HtmlResult($"<h1>created {title}</h1>");
}
