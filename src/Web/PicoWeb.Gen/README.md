# PicoWeb.Gen

PicoWeb build-time diagnostics generator. It emits **no source**; it reports `PWR001`
(info) for `app.MapGet`/`MapPost`/`MapPut`/`MapDelete` handler return types so issues
surfaced at build time instead of at request time.

## Package Info

- **Embedded in**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **Type**: Roslyn Incremental Source Generator (diagnostics only)

## Rules

| Input | Output |
|---|---|
| `app.MapGet` / `MapPost` / `MapPut` / `MapDelete` handler | `PWR001` diagnostic only — no generated code |

DTO serializers are produced by `PicoJetson.Gen` from explicit
`SerializeToUtf8Bytes<T>()` call sites, including the calls `Controllers.Gen`
emits into controller endpoint stubs.
