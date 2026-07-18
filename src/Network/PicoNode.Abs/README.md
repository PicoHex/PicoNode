# PicoNode.Abs

Core abstraction layer for PicoNode transport nodes. Defines the public interfaces consumed by implementations and end users.

## Package Info

- **NuGet**: `PicoNode.Abs`
- **TFM**: `netstandard2.0`
- **Dependencies**: `System.Threading.Channels`, `Microsoft.Bcl.AsyncInterfaces`

## Key Types

| Interface | Description |
|---|---|
| `INode` | Transport node lifecycle |
| `ITcpConnectionHandler` | TCP connection handler callbacks |
| `ITcpConnectionContext` | TCP connection context (send, close, state) |
| `IUdpDatagramHandler` | UDP datagram handler |
| `IUdpDatagramContext` | UDP datagram context |
| `NodeState` | Node state machine |
| `NodeFaultCode` | Fault code enum |
| `TcpCloseReason` | TCP close reason enum |
