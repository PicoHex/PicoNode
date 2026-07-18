# PicoWeb.Gen

PicoWeb MapMethod 源生成器。为 `builder.MapMethods<T>()` 调用生成编译时路由绑定。

## 包信息

- **嵌入于**: `PicoWeb`
- **TFM**: `netstandard2.0`
- **类型**: Roslyn Incremental Source Generator

## 生成规则

| 输入 | 输出 |
|---|---|
| `builder.MapMethods<MyHandler>()` | 自动发现 `MyHandler` 中以 `Map` 开头的方法,生成路由注册代码 |
