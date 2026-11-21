# Chorus Godot SDK — Track Evaluation and Design (EK-1)

> Decision record for the Godot engine plugin. The task book (EK-1) asks for one
> recommended track among **GDScript**, **C# (Godot 4 .NET)** and **GDExtension
> (C++)**, with the reasoning recorded before any implementation. This document
> is that record; the implementation follows the recommendation and does not run
> a second track in parallel.

## 1. The decision

**Recommended track: C# (Godot 4 .NET).**

The recommendation is not close. The deciding constraints are (a) the
cross-engine consistency core (EK-2) needs the Godot side to reproduce the
reference wire **byte-for-byte**, and (b) the acceptance criterion is an
*headless* `dotnet test` with no Godot binary in the toolchain.

| Track | Byte-parity with `JsonEnvelopeCodec` | Headless `dotnet test` | CI cost | Verdict |
|---|---|---|---|---|
| GDScript | **No** — GDScript has no `System.Text.Json`; its `JSON.stringify` differs in number formatting, string escaping, property order and null handling, so the golden Envelope bytes cannot match the reference. The 防漂移 core is exactly this byte match. | No — needs the `godot` headless binary to run `.gd` scripts. | Godot binary download per CI run. | Rejected |
| **C# (Godot 4 .NET)** | **Yes** — the same `System.Text.Json` with the same options produces identical bytes. | **Yes** — the SDK leaf is a plain class library; `dotnet test` runs it with no Godot binary. | One `dotnet build/test` on the existing .NET 10 SDK. | **Chosen** |
| GDExtension (C++) | **No** — the serializer would be reimplemented in C++; guaranteeing byte-identity with `System.Text.Json` (number/string edge cases, `JsonElement` body passthrough) is not practical and is exactly what EK-2 must pin. | No — needs the compiled extension loaded by `godot` headless. | UE-style C++ module build; heavy. | Rejected |

### 1.1 Why C# wins on the deciding constraints

1. **EK-2 is a byte-compatibility test.** The golden Envelope set asserts that the
   Unity(C#)/reference codec and the Godot codec serialize the same Envelope to
   the *same bytes* and deserialize to *equal fields*. That assertion only means
   something if both sides run the same JSON serializer semantics. Only C# does.
2. **The wire format is the contract.** `JsonEnvelopeCodec` uses
   `System.Text.Json` with `JsonSerializerDefaults.Web` + `WhenWritingNull`.
   A Godot C# leaf that restates those options reproduces the wire exactly.
3. **The SDK leaf stays engine-free.** Like the Unity SDK leaf
   (`src/Chorus.UnitySdk`, `netstandard2.1`, zero Chorus references), the Godot
   SDK leaf is a pure C# class library with no `GodotSharp` dependency. It is
   `dotnet test`-ed on the machine's .NET 10 SDK, and a Godot 4.4 .NET project
   (net8.0) references it as a normal assembly. The Godot-specific surface (a
   `Node` wrapper that surfaces the SDK's C# events as Godot signals) lives in
   the game project or a thin plugin assembly — never in the leaf.
4. **The 21-FIX-U3D legacy.** The Unity SDK already proved the pattern: a leaf
   that restates wire shapes platform-neutrally, parity-tested against the
   server contract. The Godot SDK mirrors it, so one semantic serves both
   engines and the golden test keeps them honest.

### 1.2 What the rejection costs

- **GDScript** would be the most Godot-native authoring surface, but it cannot
  reproduce the reference wire bytes and cannot run the headless `.NET` test
  lane. It would make EK-2 a weak "parse the JSON" test instead of the
  byte-parity gate the task book names.
- **GDExtension** is the only truly engine-native native path (no .NET in the
  game), but its CI cost is a C++ module build and its serializer cannot be
  pinned byte-for-byte. Both are disqualifying for a wire-protocol SDK whose
  first job is the cross-engine consistency core.

### 1.3 The honest constraint of C#

The .NET edition of Godot is a separate engine build (the standard download is
not .NET-enabled). A game that ships on the non-.NET Godot cannot use this SDK;
that is a platform reality, not a design flaw. The SDK leaf is still usable by
any .NET consumer — the `dotnet test` lane proves it — and the engine bridge is
the only Godot-4-.NET-specific part.

## 2. What the SDK is

`Chorus.GodotSdk` is a **leaf** (no `Chorus.*` project references), mirroring
`Chorus.UnitySdk`: every wire shape is restated platform-neutrally under the
`Chorus.GodotSdk` namespace, and the parity tests pin it against the server's
contract. The leaf carries:

- **Envelope framing** — a `GodotEnvelope` mirror of `Chorus.Messaging.Envelope`
  plus a `GodotEnvelopeCodec` whose `Serialize`/`Deserialize` reproduce
  `JsonEnvelopeCodec` byte-for-byte. This is the wire framing the transport
  speaks and the EK-2 golden test's subject.
- **Transport seam** — an `IChorusTransport` isomorphic to the Unity SDK's
  (Connect / Login / StartGameplay / JoinMatch / CancelMatch / AwaitRoom /
  Subscribe / SendIntent / Resume / AwaitRejoinedRoom / LeaveRound / Logout /
  LeaderboardQuery / Close), carrying the same intent/answer shapes.
- **Session lifecycle** — `ChorusGodotSdk.ConnectAsync` / `.ResumeAsync` and a
  `GodotChorusSession` with the event surface (login / match / room /
  disconnect / resume).
- **Sync applier** — a `SyncApplier` name-for-name port of the Unity SDK's, on
  `SyncFrame`/`SyncDecision`/`DeltaContractMode` restated in the leaf.

### 2.1 Engine-agnostic vs Godot-bound

| Layer | Assembly | References | Test lane |
|---|---|---|---|
| SDK leaf (this design) | `Chorus.GodotSdk` | `System.Text.Json` only | `dotnet test` (no Godot binary) |
| Godot engine bridge | game project or a `Chorus.GodotSdk.Godot` assembly | `GodotSharp` | in-editor; not in this batch's lane |

The event surface on `GodotChorusSession` is plain C# events (`event Action<...>`);
a Godot `Node` wrapper subscribes and re-emits them as Godot signals. The leaf
never depends on `GodotSharp`, so its test lane is pure .NET.

## 3. Wire framing (the Envelope contract)

The transport's wire is the Envelope framing of `Chorus.Messaging` (the IPC/bus
envelope), restated in the leaf. One Envelope per frame:

```json
{
  "type": "sdk.sync",
  "messageId": "…",
  "sessionId": "…",
  "createdTimeMs": 0,
  "body": { "…": "…" },
  "sourceEventId": "…",
  "authorityEpoch": 1
}
```

- `sourceEventId` and `authorityEpoch` are omitted when null (the reference
  codec's `WhenWritingNull`); `type`, `messageId`, `sessionId`, `createdTimeMs`
  and `body` are always present.
- The `body` is an opaque `JsonElement` — the transport's typed message payload.
  The SDK's message types name the body's shape via `type`:
  `sdk.login`, `sdk.sync`, `sdk.leaderboard`, `sdk.disconnect` (see
  `WireTypes`).

### 3.1 The Envelope vocabulary

The SDK speaks four message families on the wire (the golden set in EK-2):
login, sync, leaderboard and disconnect. Their `type` words are the SDK's own
(`sdk.*`) — they are the Godot transport's message names, not the server's IPC
types. The golden test's byte-parity is about the *codec*, not the vocabulary;
the vocabulary is what the transport maps onto the `IChorusTransport` methods.

## 4. Transport shape

`IChorusTransport` (Godot leaf) is isomorphic to the Unity SDK's
`Chorus.UnitySdk.IChorusTransport`: the same methods, the same intent/answer
shapes, the same session-security split (the SDK owns the login attempt id and
the resume secret; the transport names the tenant, gateway and connection
identities). The parity test pins the method set against the Unity leaf's.

A concrete transport (`EnvelopeTransport`) speaks the Envelope framing over a
duplex channel; the production wire (WebSocket) is the game project's concern,
and this batch delivers the framing + an in-memory duplex used by the tests —
the same split the Unity SDK keeps between its leaf and its edge transport.

## 5. Session lifecycle and event surface

`ChorusGodotSdk.ConnectAsync(configuration, adapter, ct)` mounts through the
platform adapter, opens the transport, exchanges the mount for a session,
starts the gameplay binding and answers a `GodotChorusSession` carrying the
login facts, the resume credential and the binding id — exactly the Unity SDK's
sequence. `ResumeAsync` rotates onto a fresh session without touching the
adapter. The session surfaces:

- `LoginSucceeded` (login facts), `MatchJoined` / `MatchCancelled` (match leg),
  `RoomJoined` / `RoomLeft` (room leg), `Disconnected` (wire closed),
  `Resumed` (resume facts) — plain C# events, re-emittable as Godot signals by
  a bridge `Node`.

## 6. Sync applier

A `SyncApplier(roomId, epoch, deltaContract)` deciding each `SyncFrame` against
the room, epoch and baseline with the Unity SDK's exact ordered rules —
snapshot apply / drop / rebaseline / invalid, delta invariant
`stateVersion = baseVersion + 1`, cross-epoch rebaseline, terminal result.
The parity test runs the same constructed frames through the Unity applier and
the Godot applier and asserts the same decision kinds and reason codes.

## 7. Acceptance mapping

| Task-book acceptance | How this design meets it |
|---|---|
| Godot headless test or pure .NET test | `tests/Chorus.GodotSdk.Tests` — a `dotnet test`-able xunit project, no Godot binary |
| Codec round-trip and mutual recognition with the Unity SDK | `CrossEngine` golden tests (EK-2) + the leaf's own round-trip tests |
| README ten-line integration | `Samples~/OnlineLoop` (Godot 4 C# script, compile-removed) + the SDK README example |
