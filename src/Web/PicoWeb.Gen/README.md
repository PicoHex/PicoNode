# PicoWeb.Gen

PicoWeb build-time diagnostics generator. It emits **no source**; it reports `PWR001`
(**warning**) at the registration site for `app.MapGet`/`MapPost`/`MapPut`/`MapDelete`/
`MapPatch` handler return types, so issues surface at build time instead of at request time.

## Package Info

- **Embedded in**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **Type**: Roslyn Incremental Source Generator (diagnostics only)

## Rules

| Input | Output |
|---|---|
| `app.MapGet` / `MapPost` / `MapPut` / `MapDelete` / `MapPatch` handler on `PicoNode.Web.WebApp` or `PicoNode.Http.Route<T>` | `PWR001` diagnostic only — no generated code |

`PWR001` is reported once per distinct handler return type, at the call site. A
`MapGet`-shaped method on any other type is **not** a route registration and is not
reported — the method name alone used to be the whole test, so any type with its own
`MapGet` was reported as a route handler. A block-bodied handler
(`ctx => { return …; }`) is inspected through its first `return`. The rules are tracked in
`AnalyzerReleases.Unshipped.md`.

DTO serializers are produced by `PicoJetson.Gen` from explicit
`SerializeToUtf8Bytes<T>()` call sites, including the calls `Controllers.Gen`
emits into controller endpoint stubs.
