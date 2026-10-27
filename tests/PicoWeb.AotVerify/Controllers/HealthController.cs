// The gate's controller — the only controller in this repository whose purpose is to
// be *published and executed* by the NativeAOT gate rather than only compiled.
//
// Discovery (Controllers.Gen):
//   File:   Controllers/HealthController.cs   ← /Controllers/ folder convention
//   Verb:   [HttpGet("…")]                    ← attribute wins over the method-name prefix
//   Route:  the attribute's path is absolute, so it replaces the HealthController
//           convention prefix (/api/health) entirely
//
// The verb attribute is declared here because PicoWeb ships no [HttpGet] of its own: the
// generator matches the attribute by *name* (the convention documented on
// PicoNode.Web.ControllerAttributes), and a consuming app supplies the stub — exactly as
// samples/PicoWeb.Samples does for its five verb attributes.
namespace PicoWeb.AotVerify.Controllers;

public sealed class HttpGetAttribute : Attribute
{
    public HttpGetAttribute(string? path = null) => Path = path;

    public string? Path { get; }
}

public class HealthController
{
    // Constant route: the emitted endpoint resolves the controller from DI, invokes the
    // action and serialises the returned DTO with the PicoJetson serializer that
    // [PicoJsonSerializable] generates.
    [HttpGet("/api/aot/health")]
    public HealthDto Get() => new() { Status = "generated" };

    // Route parameter: the emitted endpoint reads {id} out of the route values and parses
    // it with int.TryParse before invoking the action, so the gate also covers the binding
    // path and not only a constant route.
    [HttpGet("/api/aot/echo/{id}")]
    public HealthDto Echo(int id) => new() { Status = "echo", Echo = id };
}
