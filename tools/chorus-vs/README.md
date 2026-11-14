# chorus-vs (reserved route)

The Visual Studio host is **not built in this task** (IDE-EXT-003 note). The
route is reserved, not invented: when it happens, it consumes
[`tools/chorus-devcore`](../chorus-devcore) — the IDE-agnostic core (wire
framing, envelope protocol, attach transport with reconnect and backpressure,
the attach session facade) — the same way the VS Code shell does. A VS
extension would add only the shell: a VSIX/package around `@chorus/devcore`
plus the Visual Studio UI (tool window, status indicator, commands).

The devtools plane it attaches to is the engine's optional channel
(`--devtools`; refused in Production; off by default): UDS on posix
(`{dir}/{prefix}_devtools.sock`), a named pipe on Windows
(`\\.\pipe\{prefix}.devtools`) — both platforms the core already speaks.
