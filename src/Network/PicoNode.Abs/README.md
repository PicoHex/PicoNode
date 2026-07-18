# PicoNode.Abs

PicoNode 核心抽象层。定义 Transport Node 的公共接口,供实现层和消费方使用。

## 包信息

- **NuGet**: `PicoNode.Abs`
- **TFM**: `netstandard2.0`
- **依赖**: `System.Threading.Channels`, `Microsoft.Bcl.AsyncInterfaces`

## 核心类型

| 接口 | 说明 |
|---|---|
| `INode` | Transport node 生命周期 |
| `ITcpConnectionHandler` | TCP 连接处理器回调 |
| `ITcpConnectionContext` | TCP 连接上下文(发送、关闭、状态) |
| `IUdpDatagramHandler` | UDP 数据报处理器 |
| `IUdpDatagramContext` | UDP 数据报上下文 |
| `NodeState` | Node 状态机 |
| `NodeFaultCode` | 故障码枚举 |
| `TcpCloseReason` | TCP 关闭原因 |
