# PicoNode.Abs

Core abstraction layer for PicoNode transport nodes. Defines the public interfaces consumed by implementations and end users.

## Package Info

- **NuGet**: `PicoNode.Abs`
- **TFM**: `net10.0`
- **Dependencies**: none (zero-dependency abstraction layer)

Only the Roslyn generator projects (`Controllers.Gen`, `PicoWeb.Gen`) remain on
`netstandard2.0` for analyzer-loader compatibility.

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
