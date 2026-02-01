# RpcGen

Minimal C# source generator that produces a JSON-RPC-like client/server stub layer from **two interfaces** and a single attribute.

The repository includes a runnable demo in `RpcGen.Sample` (targets `.NET 10` in `RpcGen.Sample.csproj`).

## What gets generated

When you annotate a base class with `[RpcInterface(typeof(TOutbound), typeof(TInbound))]`, the generator emits:

1) A small runtime file: `RpcRuntime.g.cs`

- `IRpcTransport` (send bytes + async stream of received messages)
- `WebSocketTransport` (adapter over `System.Net.WebSockets.WebSocket`)
- `RpcCore` (framing, request/response correlation, dispatch)
- `RpcJson.DefaultOptions` (`System.Text.Json` options)
- `RpcRemoteException` (raised when a response contains `err`)

2) A per-base-class stub file: `*.Rpc.g.cs`

Generated members on the base class:

- `AttachTransport(IRpcTransport transport, JsonSerializerOptions? jsonOptions = null)` (protected)
- `RunAsync(CancellationToken cancellationToken = default)` (public)
- inbound dispatcher: `OnRequestAsync(...)`
- outbound stub method implementations for `TOutbound`
- request/response DTOs per method
- abstract inbound handlers for `TInbound` (you implement these in your derived concrete type)

## Project layout

- `RpcGen.SourceGenerator`
  - Contains `RpcGenerator` and the embedded runtime template `Runtime/RpcRuntime.cs`.
- `RpcGen.SourceGenerator.Test`
  - Roslyn tests that validate generated output compiles.
- `RpcGen.Sample`
  - End-to-end demo.

## Consuming the generator (csproj)

`RpcGen.Sample` references the generator as an analyzer (see `RpcGen.Sample/RpcGen.Sample.csproj`):

```xml
<ItemGroup>
  <ProjectReference Include="..\RpcGen.SourceGenerator\RpcGen.SourceGenerator\RpcGen.SourceGenerator.csproj"
                    PrivateAssets="all"
                    ReferenceOutputAssembly="false"
                    OutputItemType="Analyzer"
                    SetTargetFramework="TargetFramework=netstandard2.0" />
</ItemGroup>
```

You also need a normal reference to `RpcGen.Abstractions` for `[RpcInterface]`:

```xml
<ItemGroup>
  <ProjectReference Include="..\RpcGen.Abstractions\RpcGen.Abstractions.csproj" />
</ItemGroup>
```

## How to use

RpcGen expects *two* interfaces to model duplex communication:

- `TOutbound`: methods you call on the remote endpoint
- `TInbound`: methods the remote endpoint can call on you

### 1) Define two interfaces

Example names from the demo:

- `IClientRpcInterface`: client ? server
- `IServerRpcInterface`: server ? client

Supported method return types (both inbound and outbound):

- `void` (notification)
- `Task`
- `Task<T>`

### 2) Define two base classes and annotate them

You create *one base class per endpoint role* and annotate each with opposite directions.

```csharp
using RpcGen;

[RpcInterface(typeof(IServerRpcInterface), typeof(IClientRpcInterface))]
internal abstract partial class ServerAppRpcBaseClass : IServerRpcInterface
{
}

[RpcInterface(typeof(IClientRpcInterface), typeof(IServerRpcInterface))]
internal abstract partial class ClientAppRpcBaseClass : IClientRpcInterface
{
}
```

### 3) Implement the inbound handlers in concrete types

The generator emits abstract methods for `TInbound`. Your concrete types implement them:

- `ServerApp : ServerAppRpcBaseClass` implements handlers for `IClientRpcInterface`.
- `ClientApp : ClientAppRpcBaseClass` implements handlers for `IServerRpcInterface`.

### 4) Attach a transport and run the receive loop

Each base class gets:

- `AttachTransport(...)` to wire an `IRpcTransport`
- `RunAsync(...)` to start reading and dispatching inbound messages

Minimal wiring pattern:

```csharp
// server.AttachTransport(serverTransport);
// client.AttachTransport(clientTransport);

var serverLoop = server.RunAsync(ct);
var clientLoop = client.RunAsync(ct);
```

Inbound dispatch is driven by the JSON fields produced/consumed by `RpcCore`:

- `id`: request id
- `i`: interface name
- `m`: method name
- `k`: kind (`Request`, `Response`, `Notification`)
- `p`: payload object
- `err`: error string (responses only)

## Transport model

`IRpcTransport` is intentionally small:

- `SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)`
- `ReadAsync(CancellationToken cancellationToken)` yielding `IAsyncEnumerable<ReadOnlyMemory<byte>>`

`RpcCore` assumes each yielded chunk from `ReadAsync` is a complete JSON message.

The runtime includes `WebSocketTransport` as an adapter over a `WebSocket` which reads full text messages and yields them as UTF-8 bytes.

## RPC semantics

### Outbound

Outbound interface methods are implemented on the generated base class:

- `void` ? notification (`RpcCore.NotifyAsync`)
- `Task` / `Task<T>` ? request/response (`RpcCore.RequestAsync`)

### Inbound

Inbound calls are dispatched by `OnRequestAsync(...)` to the abstract handler methods you implement.

- `void` inbound handlers do not send a response.
- `Task` / `Task<T>` inbound handlers send a response payload back to the caller.

## Demo: `RpcGen.Sample`

`RpcGen.Sample` is a minimal end-to-end demo showing:

- one server and one client (separate concrete types)
- an in-memory paired transport
- client ? server requests and server ? client notifications

Entry point: `RpcGen.Sample/Program.cs`.

## Notes / limitations

- Serialization uses `System.Text.Json`.
- Generated DTO property names are derived from parameter names (first letter uppercased).
- Only ordinary interface methods are considered (no properties/events).
- Return types beyond `void`, `Task`, and `Task<T>` are treated as unsupported.
