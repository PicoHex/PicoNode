# Controllers.Gen

PicoWeb 控制器源生成器。扫描实现 `IController` 的类,自动生成路由注册代码。

## 包信息

- **嵌入于**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **类型**: Roslyn Incremental Source Generator

## 生成规则

| 输入 | 输出 |
|---|---|
| `class MyController : IController` | `WebApiBuilder` 扩展方法自动注册所有 `[Route]` 方法 |
