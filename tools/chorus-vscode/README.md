# chorus-vscode

The Chorus VS Code shell (IDE-EXT): attach to a local engine's optional
devtools channel and watch it — status bar link state with live CPU, a panel
with CPU / working-set curves, the engine log stream (filter + level), the
`/sessions` snapshot, and a 30 s flame graph sample entry. Read-only: the
extension never mutates the engine.

The core (framing, protocol, transport, reconnect, backpressure) lives in
[`tools/chorus-devcore`](../chorus-devcore) and rides in from source via
tsconfig paths — this package is the thin shell the task calls for, and a
future Visual Studio host repeats the move with the same core.

## The engine side

The devtools channel is **off by default**; turn it on when you launch the
engine:

```bash
dotnet src/Chorus.EngineHost/bin/Release/net10.0/Chorus.EngineHost.dll --devtools
```

(Production hosts refuse the flag outright — it is a development surface.)
Settings: `chorus.unixSocketDir` (default `/tmp/chorus`),
`chorus.pipePrefix` (default `chorus`), `chorus.engineUrl` (default
`http://127.0.0.1:5067`, the engine dev HTTP face for `/sessions`).

## Commands

| command                    | what                                                             |
| -------------------------- | ---------------------------------------------------------------- |
| `Chorus: Attach to Engine` | Connect (the reconnect loop chases the engine until it appears)   |
| `Chorus: Detach`           | Close the link                                                   |
| `Chorus: Open Panel`       | The curves / log stream / sessions view                          |
| `Chorus: Sample Flame Graph (30s)` | `dotnet-trace` the engine pid → `.speedscope.json` under the OS temp dir, then open [speedscope.app](https://speedscope.app) to drop the file on. Needs `dotnet tool install -g dotnet-trace`. |

## Development

```bash
pnpm install
pnpm typecheck   # tsc --noEmit (strict)
pnpm test        # vitest — the pure logic (flame argv, panel page contract)
pnpm smoke       # a real headless VS Code runs dist-smoke/smokeSuite.js
pnpm package     # vsce package → chorus-vscode-0.0.0.vsix (version stays 0.0.0; not published)
```
