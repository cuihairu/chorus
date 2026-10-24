---
title: Versioning Policy and Compatibility
---


# Versioning Policy and Compatibility

Related documents:

- [Deployment and Security](./deployment.md)
- [Protocol Reference](./protocol.md)
- [Observability](./observability.md)

## Version Numbers

- Semantic versioning `MAJOR.MINOR.PATCH` (SemVer 2.0).
- The 0.x phase allows breaking changes; change records live in commit messages and `docs/design/review-*.md`.
- From 1.0 onward it is committed that only backward-compatible changes are allowed within the same major version.
- **A formal release is triggered only when the maintainer explicitly supplies a version number**: push a `v*` tag → CI build + test → only after passing is a GitHub Release created. There is no automatic tagging, and binaries that have not passed tests are not put into Releases. Daily builds/rolling binaries, if enabled, are published only as rolling artifacts and do not carry a formal version number.

## Compatibility Surfaces

### 1) WebSocket Wire Protocol (Client ↔ Gateway)

| Message | Direction | Field Evolution Rule |
| --- | --- | --- |
| `hello` | C→S | Adding an optional field is OK; changing semantics / removing a field = breaking |
| `welcome` | S→C | Same as above; tick parameters are added only, never removed |
| `ack` | C→S | Same as above |
| `snapshot` | S→C | Same as above; the content of `state` is determined by the `game` module |
| `ping` / `pong` / `error` | Bidirectional | Same as above |

- `ClientHello.ClientVersion` is a free string and is **not** a protocol version negotiation field.
- Messages with an unknown `type` are rejected/ignored and do not cause the connection to close.
- `hello.protocolVersion` / `welcome.protocolVersion` (since 2026-10-12, see [Protocol Reference](./protocol.md)):
  default is treated as a legacy client and allowed through; if sent and not equal to the server's `ProtocolVersionPolicy.Current` → rejected with `protocol_mismatch`.
  Parts that remain a **known gap**: this field is a single-value check rather than range negotiation (the server cannot declare "supports ≤N"),
  and when the server upgrades to a new major version, legacy clients need to read `welcome.protocolVersion` and disconnect proactively.

### 2) Snapshot State Schema

- Engine-side snapshots carry `V = 1` (schema version).
- The version number has been lifted to the wire protocol's `ServerSnapshot` `v` field (2026-10-13, 2d157fa): engine snapshots are passed through (both the broadcast path `WireSnapshot` and the eng→gw IPC path `EngineBusSessionClient` carry it, and the gateway forwards it as-is); currently always `1`; when a legacy sender omits the field, the receiver reads it as the default `1`, compatible in both directions.
- The remaining half (1.0): explicit rejection of an unknown `v` + forced resync semantics — to be decided when the first v2 schema is designed; there is currently only one version so there is nothing to trigger it; during the transition, a client that encounters an unrecognized `v` can just drop it + keep ACKing + wait for resync as the fallback.
- Restore semantics (2026-10-14, `062cc68`): module restoration from a snapshot goes through `IGameModule.RestoreState(version, JsonElement)`, where the input is the wire protocol JSON and `version` has the same semantics as the wire protocol's `v`; an unknown version / exceeding `Session:MaxSnapshotBytes` / a module exception all fail-closed and reject session startup. Schema evolution rules are the same as the wire protocol (adding an optional field is compatible; removing/changing semantics is breaking).

### 3) Ingress Interface (Platform ↔ Gateway)

- The URL shape `POST /ingress/{platform}` is unchanged; adding a platform name is compatible.
- Body structure is defined by each platform mapper; adding an optional field is compatible, removing/changing semantics is breaking.

### 4) IPC (Gateway ↔ EngineHost)

- Adding an optional field to `Envelope` (e.g. `SourceEventId`) is backward compatible; a new `Type` is handled by the receiver as an unknown type.
- Changing `Ipc:Transport` requires a synchronized restart of both sides.

### 5) Configuration

- Unknown keys are ignored; keys that are removed / have changed semantics = breaking change and must be noted in the release notes.
- Changes to `Session:*` defaults are non-breaking (they take effect when not configured); explicit configuration takes precedence.

### 6) Module Contract (`IGameModule`, Engine Internal)

- `RestoreState(long version, JsonElement state)` is a required member of `IGameModule` (2026-10-14,
  `062cc68`): a breaking change (0.x window; the implementation surface is only the in-repo `SampleGameModule` + test fakes). Restore semantics
  are described in §2 above; the engine does not provide a built-in default restore implementation, and module authors are responsible for state reconstruction themselves.
- From 1.0+ onward, `IGameModule` contract changes (adding/changing-semantics members) are treated as breaking and must be noted in the release notes.

## Compatibility Decision Rules

| Change Type | 0.x | 1.0+ |
| --- | --- | --- |
| Wire protocol adds an optional field | Allowed | Allowed |
| Wire protocol adds a message type | Allowed | Allowed |
| Remove/rename a field | Allowed (noted in release notes) | Forbidden |
| Config adds a key | Allowed | Allowed |
| Config removes/changes semantics | Allowed (noted in release notes) | Forbidden |
| Add a platform adapter | Allowed | Allowed |
| `Session:*` default change | Allowed (noted in release notes) | Cautious (explicit configuration takes precedence) |
| `IGameModule` contract change | Allowed (0.x, noted in release notes) | Forbidden |

## Persistence and Data Migration

Standalone Server session state is in memory and has no restart recovery. ControlHost mode persists fenced room checkpoints, so those deployments must keep checkpoint schema and `IGameModule.RestoreState` compatibility in view during upgrades. Wire protocol and configuration compatibility rules still apply independently.

## Upgrade Flow (Operations Perspective)

1. Upgrade `EngineHost` first, then `Gateway` (old and new Gateways can coexist because they do not share in-memory state; but the IPC configuration must be consistent).
2. Recommended rollout order: single-process form first, then split form.
3. Rollback = restart the old version's process; clients automatically reconnect and resync, no coordination needed.
