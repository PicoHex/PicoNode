# PicoNode.Web.Session.Abs

Session abstraction layer for PicoNode.Web. Defines the public interfaces for session management.

## Package Info

- **NuGet**: `PicoNode.Web.Session.Abs`
- **TFM**: `net10.0`
- **Dependencies**: none (BCL only)

## Key Types

| Type | Description |
|---|---|
| `ISession` | Individual session: `Id`, `IsNew`, `IsDirty`, key-value storage (`TryGetValue`/`SetValue`/`Remove`/`Clear`/`Keys`) |
| `ISessionStore` | Session storage: `LoadAsync`, `CreateAsync`, `SaveAsync`, `DeleteAsync` |
| `SessionOptions` | Session configuration: `IdleTimeout`, `CleanupInterval` |
