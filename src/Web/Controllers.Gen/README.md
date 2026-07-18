# Controllers.Gen

PicoWeb controller source generator. Scans classes implementing `IController` and auto-generates route registration code.

## Package Info

- **Embedded in**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **Type**: Roslyn Incremental Source Generator

## Rules

| Input | Output |
|---|---|
| `class MyController : IController` | `WebApiBuilder` extension method auto-registers all `[Route]` methods |
