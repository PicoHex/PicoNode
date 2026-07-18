# PicoNode.Web.Session.Abs

Session abstraction layer for PicoNode.Web. Defines the public interfaces for session management.

## Package Info

- **NuGet**: `PicoNode.Web.Session.Abs`
- **TFM**: `netstandard2.0`
- **Dependencies**: `Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`

## Key Types

| Type | Description |
|---|---|
| `ISession` | Individual session: Id, CreatedAt, LastAccessed, key-value store |
| `ISessionStore` | Session storage: CreateAsync, GetAsync, TouchAsync, DestroyAsync |
| `SessionOptions` | Session config: IdleTimeout, cookie options |
