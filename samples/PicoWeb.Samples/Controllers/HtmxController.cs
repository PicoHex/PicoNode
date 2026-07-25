using PicoHtmx;
using PicoNode.Web;

namespace PicoWeb.Samples.Controllers;

[ApiController]  // Works outside Controllers/ folder too
public class HtmxController
{
    public HtmxResult GetPage() =>
        new HtmxResult("<h1>HTMX Controller Works!</h1>");

    public HtmxResult GetRedirectTest() =>
        new HtmxResult("").WithRedirect("/api/htmx/page");

    public HtmxResult PostSubmit() =>
        new HtmxResult("<p>Submitted!</p>")
            .WithTrigger("""{"toast":{"message":"saved"}}""");
}
