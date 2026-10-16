---
title: Communication Abstraction (In-Process / Inter-Process)
---

<p align="center">
  English | <a href="ipc.zh.md">简体中文</a>
</p>

# Communication Abstraction (In-Process / Inter-Process)

The approach you mentioned is correct: encapsulate "in-process communication / inter-process communication" uniformly as a single `channel` (message queue), provide an Actor (single-threaded mailbox) model on top of it, so the upper layers only face `Tell/Publish`, thereby shielding the underlying details.

## Unified Abstraction

- **Channel (message delivery)**: responsible for delivering `Envelope` to some "destination"
- **Actor (ordered processing)**: each `sessionId` corresponds to one actor, guaranteeing that messages of the same session are processed serially
- **Router (routing)**: decides which actor/which process the messages of the same `sessionId` should land on

## Semantic Differences (Must Be Handled Explicitly)

Even though the abstraction is unified, inter-process communication brings unavoidable semantic differences:

- **Delivery semantics**: IPC/network is generally at-least-once (may duplicate) → must be idempotent/deduplicated
- **Ordering**: rely on `orderingKey=sessionId` for partitioning/routing, otherwise messages will be out of order
- **Backpressure**: when the queue backs up, there must be rate limiting/downsampling/snapshot catch-up strategies

## Transport Layer Priority (Localhost First)

Layered by "implementation complexity vs performance gain" (the order you mentioned is also reasonable):

1. **In-process queue (Channel)**: fastest and most reliable (Monolith default)
2. **Local IPC (Unix Domain Socket / NamedPipe)**: cross-process but still on the same machine, simple operations
3. **localhost TCP**: more general for cross-process/cross-container (a transitional solution before splitting into containers/multiple hosts)
4. **Remote TCP/message queue**: multi-machine deployment
5. **Shared memory (MemoryMappedFile + RingBuffer + FlatBuffers)**: highest throughput, but the largest implementation and debugging cost (recommended only after a clear performance bottleneck; for detailed evaluation and trigger conditions see [Shared Memory IPC Transport Evaluation](./shared-memory-transport.md))

## Current Repository State

- The monolith (`InProc`) is still the default deployment form; split mode (Gateway + EngineHost) is implemented and usable (see [Split Mode](../guide/split-mode.md))
- `Chorus.Messaging` is the channel abstraction and transport layer:
  - `InProcMessageBus`: in-process queue
  - `NamedPipeMessageBus`: local inter-process IPC (Windows friendly)
  - `UnixDomainSocketMessageBus`: local inter-process IPC (Linux/macOS; newer Windows versions support AF_UNIX)
  - `TcpMessageBus`: localhost TCP (cross-container/cross network namespace)
  - `HttpMessageBus`: HTTP one-way delivery (behind a remote gateway layer / reverse proxy)
  - `FanOutMessageBus`: publisher-side fanout wrapper, publishes to N child buses one by one, a single failure doesn't block the rest, failures are aggregated and thrown

### Channel Naming and Multiple Gateways

`IpcEndpointBuilder.Build` generates a pair of channels:

- gw→eng (commands): `{prefix}_gw_to_eng.sock` (UDS) / `{prefix}.gw_to_eng` (NamedPipe), not distinguished per gateway, shared by all gateways, with a single server on the engine side as the sink
- eng→gw (snapshots/welcome/heartbeat): one per gateway. The Gateway configures its own channel with a `Gateway:Id` suffix (`{prefix}_eng_to_gw_{id}.sock` / `{prefix}.eng_to_gw.{id}`); if id is empty, use the old name (`{prefix}_eng_to_gw.sock`). EngineHost configures `Ipc:GatewayIds` to list all gateway ids, creating one client per id's channel and fanning out

TCP/HTTP transport ports and URLs are not distinguished by id; multiple gateways on the same machine sharing a worker use UDS/NamedPipe; the port scheme for multi-machine multiple gateways belongs to phase three (see [Process Topology](./process-topology.md) §4).

### Socket File Lifecycle (2026-10-16 Decision)

The UDS socket file is the path entry created by bind, and ownership of the right to delete it previously caused a race: since .NET 6, `Socket.Dispose` unconditionally unlinks the path bound with `UnixDomainSocketEndPoint` (dotnet/runtime#52103, no opt-out). During a rolling restart the new process deletes-binds first, snatching the same-named path, and the old process disposes afterwards — it unlinks by **path**, cutting off the file entry of the new process's socket: the kernel socket is not dead, but all subsequent connections get ENOENT (previously load tests worked around it with a manual `rm -f /tmp/chorus/*.sock`, and this was the root cause).

Decision (proxy-decided): `UnixDomainSocketMessageBus` does server-side bind with a custom `RawUnixEndPoint` (self-managed sockaddr_un serialization, layout identical to `UnixDomainSocketEndPoint`). The BCL's deletion tracking only recognizes the `UnixDomainSocketEndPoint` type itself (sealed), so a custom endpoint doesn't enter the tracking, and the path deletion right returns to the bus itself; on close, delete under a **bind identity guard** — record the `CreationTimeUtc` of the file at the path at bind time (st_ctime on Unix, metadata no longer changes after bind), and only delete when the file is still the one this process bound; a same-named file re-bound by a new process must never be deleted by mistake. The deletion action is hooked to `DisposeAsync` (the tail of the accept loop is unreliable: a `Task.Run` task with a cancellation token may never be scheduled). Residual files from an abnormal exit are covered by the next `StartServer`'s delete-bind flow, and when the identity is unknown, conservatively don't delete. The client's `ConnectAsync` keeps the BCL endpoint (never binds, doesn't enter deletion tracking).

## Regarding Windows "Support for Unix Sockets"

Windows's AF_UNIX (Unix Domain Socket) support still varies by system version and runtime environment (e.g. containers, permissions, path rules); therefore at the framework level it is recommended:

- Default strategy: **as long as the runtime environment supports UDS, use UDS**
- Only downgrade in the following cases: the runtime environment doesn't support it, or UDS bind/connect fails (e.g. old systems, path/permission restrictions)
  - Downgrade to: NamedPipe (native and mature on Windows)

## Transport Selection and Configuration

Manual selection is provided on `Ipc:Transport`, to be compatible with the different deployment forms of "single process / split process / split container":

- `Auto`: prefer UDS (use it if available), downgrade to NamedPipe on failure/unsupported
- `UnixDomainSocket`: force UDS
- `NamedPipe`: force NamedPipe
- `Tcp`: force localhost TCP (requires fixed port configuration, suitable for splitting into containers or scenarios needing explicit port mapping)
- `Http`: force HTTP one-way delivery (better suited to remote gateway layers, reverse proxies, simple cross-host connectivity)

New configuration items added for TCP mode (both ends must be consistent):

- `Ipc:TcpHost`: default `127.0.0.1`
- `Ipc:TcpGatewayToEnginePort`: Gateway -> Engine (command channel) listening port, default `27500`
- `Ipc:TcpEngineToGatewayPort`: Engine -> Gateway (event/snapshot channel) listening port, default `27501`

New configuration items added for HTTP mode (both ends must be consistent):

- `Ipc:HttpGatewayToEngineUrl`: Gateway -> Engine (command channel) receiving address, default `http://127.0.0.1:27600/gw-to-eng/`
- `Ipc:HttpEngineToGatewayUrl`: Engine -> Gateway (event/snapshot channel) receiving address, default `http://127.0.0.1:27601/eng-to-gw/`
