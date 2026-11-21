---
title: "Engine plugin matrix: Unity, Godot and Unreal client surfaces"
---

# Engine plugin matrix: Unity, Godot and Unreal client surfaces

> EK-5 deliverable (todo.md EK-5). The server hosts a game session; a game engine
> client talks to it over the Chorus wire. This page is the honest status of the
> three engine surfaces. "Implemented" means the surface exists and its tests are
> green; "not implemented" means the face is genuinely absent — the boundary is
> written as it is, not as it is hoped to be. Each engine integrates the **same**
> protocol and session semantics, not a parallel variant; the cross-engine golden
> set ([protocol](../reference/protocol.md), EK-2) pins the wire so a change on one
> side is mirrored on the others or the suite turns red.

## Status and integration

| Engine | Status | Artifact | Integration path |
| --- | --- | --- | --- |
| Unity | **Implemented** (reference SDK, 21 FIX-U3D items) | `com.chorus.sdk` — `src/Chorus.UnitySdk` | Unity Package Manager (local folder or tarball); game code references the assembly and programs against `ChorusSdk` / `ChorusSession`. |
| Godot | **Implemented** (Godot 4.4+, .NET track) | `Chorus.GodotSdk` — `src/Chorus.GodotSdk` | Reference the assembly in a Godot 4.4+ .NET project; the `Samples~/` `Node` bridge shows the adapter and the signal re-emission. |
| Unreal (UE5) | **Not implemented** — deferred | — `src/Chorus.UnrealSdk/REVIEW.md` | Pending. The review concludes a C++ `Runtime` module re-implementing the same contract, deferred until a UE toolchain is provisioned. |

The Godot leaf is a pure `net8.0` class library with **no** `GodotSharp` and **no**
Chorus references; the Unity leaf is the same shape (`netstandard2.1`, engine-agnostic).
That is what lets either drop into its engine as a normal assembly. Unreal has no
sanctioned .NET host, so the UE path is a C++ re-implementation of the *contract*
(see the review's reuse-boundary section), not a shared binary.

## Coverage

"Covered" is the honest face each engine exposes today; anything absent is written
absent. The wire itself is the one contract every engine must reproduce; the golden
tests assert it.

| Capability | Unity | Godot | Unreal |
| --- | --- | --- | --- |
| Envelope framing (byte-identical to `JsonEnvelopeCodec`) | Edge wire is the flat-frame surface (documented; the reference codec is the server side) | **Covered** — `GodotEnvelopeCodec`, byte-parity asserted | **Not implemented** |
| Transport binding (`IChorusTransport`-isomorphic seam) | **Covered** | **Covered** — `EnvelopeTransport` / `IEnvelopeChannel` | **Not implemented** |
| Session lifecycle (Connect / Resume) | **Covered** | **Covered** — `ChorusGodotSdk` / `GodotChorusSession` | **Not implemented** |
| SyncApplier (same decision ladder) | **Covered** | **Covered** — parity asserted cross-engine | **Not implemented** |
| Event surface (match / room / disconnect) | **Covered** | **Covered** — `MatchJoined`, `RoomJoined`, `RoomLeft`, `Disconnected` | **Not implemented** |
| Leaderboard reads (Top / Rank / Around) | **Covered** | **Covered** — `GetTopAsync`, `GetRankAsync`, `GetAroundMeAsync` | **Not implemented** |
| Headless test lane | `dotnet test` (in `Chorus.sln`) | **Covered** — `Chorus.GodotSdk.Tests`, plain `dotnet test`, no Godot binary | **Not implemented** |

## Cross-engine consistency core (EK-2)

The golden set in `tests/Chorus.Engine.Tests/CrossEngine/` pins the reference
`JsonEnvelopeCodec` against the Godot `GodotEnvelopeCodec` byte-for-byte over a
canonical Envelope set (login / sync / leaderboard / disconnect) and asserts the
Unity and Godot `SyncApplier`s answer the same decision and baseline on the same
frames. The suite turns red on a wire or decision change that is not mirrored —
this is the anti-drift guard, and it runs in the main CI must-run set.

## Ten-line Godot integration

See [`src/Chorus.GodotSdk/README.md`](../../src/Chorus.GodotSdk/README.md) for the
shortest complete Godot loop (adapter + connect + subscribe + applier).
