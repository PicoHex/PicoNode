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
| Controller classes (above) | `Controllers_Endpoints.g.cs` — one `{FullName}_Endpoints` class per controller registering one `MapGet/MapPost/MapPut/MapDelete/MapPatch` handler per HTTP method. The stub name is keyed by the controller's **fully-qualified** name (`global::` stripped, `.`/`+`/`<`/`>` → `_`), so `Admin.UsersController` and `Public.UsersController` produce two distinct stubs instead of colliding (CS0101) |
| Skipped controller | `SkippedControllers.g.cs` — a comments-only note naming every controller the generator refused to emit, so a skip is visible instead of presenting as a 404 at run time. Currently: **generic** controllers, whose type parameters cannot be bound in the generated endpoint map (CS0246) |
| Same | `EndpointRegistrar.g.cs` — `public static class EndpointRegistrar` with `RegisterAll(WebApp app)`; consumers must call it to wire routes (see README). Skipped when the compilation already carries an `EndpointRegistrar` and this project has no controllers; with no controllers and no existing registrar, a no-op `RegisterAll(object)` shim keeps the call compiling |
| Same | `ControllerServiceRegistrations.g.cs` — `[ModuleInitializer]` that registers every non-static controller in PicoDI via `SvcContainerAutoConfiguration.RegisterConfigurator` (Scoped lifetime) |

## Route Convention

| Rule | Example |
|---|---|
| Prefix: `{ClassName}` minus `Controller` suffix → `/api/{kebab-case}` | `UsersController` → `/api/users` |
| Verb: method name prefix (`Get`/`Post`/`Put`/`Delete`/`Patch`) or `[HttpGet]`-style attribute | `GetUser` → GET |
| Path: method name minus verb prefix, kebab-cased | `GetUser` → `/user` |
| Params: simple-typed parameters bind from the route value, else the query string, else 400 — no `{name}` segment is invented — and non-string types keep their TryParse guard (404 on mismatch) | `GetUser(int id)` → `/api/users/user/{id}`, `[HttpPost] Create(string title)` → `POST /api/posts?title=...` |
| Attribute override: `[Route(...)]` on class, `[HttpGet("...")]`-style on method | absolute or relative path |

Complex-typed parameters and `WebContext`/`CancellationToken` are resolved at runtime
(`ctx.Services` / handler args), not from the route.

## Notes

- **Genuinely incremental**: the value the pipeline caches is the controller model — strings,
  bools and `EquatableArray<T>`, with no `ISymbol` or `Compilation` in it — so a run whose
  models come out equal does not re-enter the emitters. The syntax transform itself is not
  cached by Roslyn (it re-runs per class declaration on each pass); what value equality
  saves is the generated text. Pinned by
  `ControllersGeneratorTests.Generator_output_is_cached_across_an_unrelated_edit` and
  `...Only_the_changed_controller_is_rebuilt`, which assert the driver's step reasons.
- The generator emits **no `[PicoJsonSerializable]` markers** — apply the attribute to DTOs
  directly for PicoJetson.Gen serialization.
- `EndpointRegistrar` lives in the **global namespace** — do not declare your own type
  with that name in a project that owns controllers.
- Imported registrar: an Exe referencing an application that already exports
  `EndpointRegistrar` (e.g. an integration-test project referencing a sample app) does not
  get a second one — `RegisterAll` binds to the referenced app's registrar. Without this
  rule the two public types collide (CS0436, a hard error under warnings-as-errors) and the
  local empty shim silently swallows the referenced app's endpoint registration.
