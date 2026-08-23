# Controllers.Gen

PicoWeb controller source generator. Discovers controller classes and auto-generates
route registration + DI registration code.

## Package Info

- **Embedded in**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **Type**: Roslyn Incremental Source Generator

## Discovery Rules

A class is a controller when **either** holds:

1. **Folder convention** — the source file lives in a `Controllers/` directory segment, or
2. **Attribute** — the class is decorated with `[ApiController]` (or a custom `[ApiControllerAttribute]`).

Only executable projects (`ConsoleApplication` / `WindowsApplication`) that reference
`PicoNode.Web.WebApp` get generated endpoints — library projects are skipped.

## Generated Output

| Input | Output |
|---|---|
| Controller classes (above) | `Controllers_Endpoints.g.cs` — per-controller `{Name}_Endpoints` classes registering one `MapGet/MapPost/MapPut/MapDelete/MapPatch` handler per HTTP method |
| Same | `EndpointRegistrar.g.cs` — `public static class EndpointRegistrar` with `RegisterAll(WebApp app)`; consumers must call it to wire routes (see README) |
| Same | `ControllerServiceRegistrations.g.cs` — `[ModuleInitializer]` that registers every non-static controller in PicoDI via `SvcContainerAutoConfiguration.RegisterConfigurator` (Scoped lifetime) |

## Route Convention

| Rule | Example |
|---|---|
| Prefix: `{ClassName}` minus `Controller` suffix → `/api/{kebab-case}` | `UsersController` → `/api/users` |
| Verb: method name prefix (`Get`/`Post`/`Put`/`Delete`/`Patch`) or `[HttpGet]`-style attribute | `GetUser` → GET |
| Path: method name minus verb prefix, kebab-cased | `GetUser` → `/user` |
| Params: simple-typed parameters become `{name}` route segments with TryParse guards (404 on mismatch) | `GetUser(int id)` → `/api/users/user/{id}` |
| Attribute override: `[Route(...)]` on class, `[HttpGet("...")]`-style on method | absolute or relative path |

Complex-typed parameters and `WebContext`/`CancellationToken` are resolved at runtime
(`ctx.Services` / handler args), not from the route.

## Notes

- The generator emits **no `[PicoJsonSerializable]` markers** — apply the attribute to DTOs
  directly for PicoJetson.Gen serialization.
- `EndpointRegistrar` is emitted into the **global namespace** — do not define your own
  type with that name in a project referencing Controllers.Gen.
