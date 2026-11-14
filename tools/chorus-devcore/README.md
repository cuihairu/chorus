# @chorus/devcore

IDE-agnostic core for the Chorus devtools plane (IDE-EXT): the wire framing,
the envelope protocol, and the attach transport that an IDE shell (VS Code
today, Visual Studio later) consumes to watch a running engine — live CPU /
memory snapshots and the engine's log stream, read-only.

The engine side is the `Chorus.DevTools` hosted service: **off by default**,
turned on with `--devtools` (or `DevTools:Enabled=true`), refused outright in
the Production environment. Nothing binds without the flag:

```bash
dotnet src/Chorus.EngineHost/bin/Release/net10.0/Chorus.EngineHost.dll --devtools
```

The service binds `{Ipc:UnixSocketDir}/{Ipc:NamedPipePrefix}_devtools.sock`
(defaults `/tmp/chorus/chorus_devtools.sock`), or the named pipe
`\\.\pipe\{prefix}.devtools` on Windows — the same `Ipc:` section that drives
the gateway channels drives this one.

## Surfaces

| module       | what                                                              | mirrored from                                             |
| ------------ | ----------------------------------------------------------------- | --------------------------------------------------------- |
| `frame.js`   | u32 **LE** length + payload, 32 MiB bound, zero-length legal      | `Chorus.Messaging/Transports` (`FrameReader`)              |
| `envelope.js`| camelCase envelope `{ type, messageId, sessionId, createdTimeMs, body, sourceEventId?, authorityEpoch? }` + typed `dev.log` / `dev.metrics` bodies | `Chorus.Messaging/Envelope.cs`, `Chorus.DevTools` |
| `transport.js` | push-only socket client, reconnect with backoff, bounded drop-oldest delivery queue | `Chorus.DevTools/DevToolsService.cs` |
| `attach.js`  | the session facade IDE shells consume                             | —                                                          |

## Usage

```ts
import { AttachSession } from "@chorus/devcore";

const session = new AttachSession({
  transport: { endpoint: AttachSession.endpoint(process.platform) },
});

session.on("metrics", (snapshot) => chart.push(snapshot.cpuPercent));
session.on("log", (entry) => logView.append(entry));
session.on("link-state", (state) => statusBar.update(state));

await session.attach();
// …
session.detach();
```

Delivery backpressure is a bounded drop-oldest queue (default 4096 frames,
drained one batch per event-loop turn): a log burst drops the oldest local
frames instead of stalling the loop, and instead of pausing the socket — the
engine drops readers whose writes stall for 2 s, so holding the kernel buffer
hostage risks the whole attach. `backpressure` carries the depth and drop
count; `transport.stats` reports `dropped` / `skippedEmptyFrames` /
`malformedFrames`.

## Development

```bash
pnpm install
pnpm typecheck   # tsc --noEmit (strict)
pnpm test        # vitest — framing, envelope shapes, reconnect, backpressure
pnpm build       # dist/ (NodeNext ESM + declarations)
```

The transport tests ride real unix domain sockets (a fake engine that accepts
and pushes the same way `DevToolsService` does), not mocks of the framing.
