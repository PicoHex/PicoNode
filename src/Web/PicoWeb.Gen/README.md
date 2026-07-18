# PicoWeb.Gen

PicoWeb MapMethod source generator. Generates compile-time route bindings for `builder.MapMethods<T>()` calls.

## Package Info

- **Embedded in**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **Type**: Roslyn Incremental Source Generator

## Rules

| Input | Output |
|---|---|
| `builder.MapMethods<MyHandler>()` | Auto-discovers methods prefixed with `Map` on `MyHandler`, generates route registration code |
