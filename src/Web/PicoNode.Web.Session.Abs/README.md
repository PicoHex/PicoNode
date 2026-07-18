# PicoNode.Web.Session.Abs

PicoNode.Web Session 抽象层。定义会话管理的公共接口。

## 包信息

- **NuGet**: `PicoNode.Web.Session.Abs`
- **TFM**: `netstandard2.0`
- **依赖**: `Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`

## 核心类型

| 类型 | 说明 |
|---|---|
| `ISession` | 单个会话: Id, CreatedAt, LastAccessed, 键值存储 |
| `ISessionStore` | 会话存储: CreateAsync, GetAsync, TouchAsync, DestroyAsync |
| `SessionOptions` | 会话配置: IdleTimeout, Cookie 选项 |
