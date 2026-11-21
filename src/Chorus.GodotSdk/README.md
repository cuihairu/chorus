# Chorus Godot SDK

The Chorus client SDK for **Godot 4 .NET** games (EK-1). It mirrors the Unity SDK
(`src/Chorus.UnitySdk`) so one protocol serves both engines: the same transport
seam, the same session lifecycle, the same sync applier — and the same Envelope
framing, which the cross-engine golden tests (EK-2) pin byte-for-byte against the
server's `JsonEnvelopeCodec`.

- The **leaf** (`Chorus.GodotSdk`) is a pure C# class library (net8.0, zero
  Chorus references) — a Godot 4.4 .NET project references it as a normal
  assembly. No `GodotSharp` in the leaf; its test lane is plain `dotnet test`.
- The **engine bridge** (a `Node` that re-emits the session's C# events as Godot
  signals) lives in the game project or a thin plugin assembly — see `Samples~`.
- The **wire** is the game project's concern: plug a socket into the
  `IEnvelopeChannel` the transport speaks over.

## Ten-line integration

```csharp
using Chorus.GodotSdk;

// 1. The SDK over a channel (the wire); the game owns the channel's socket.
var sdk = new ChorusGodotSdk(cfg => new EnvelopeTransport(channel));

// 2. React to the session (subscribe before the connect, so match/room land on you).
sdk.SessionEstablished += session => {
    session.MatchJoined  += ticket => GD.Print("matched: ", ticket.RoomId);
    session.RoomJoined   += room   => GD.Print("room: ", room.RoomId, " epoch ", room.RoomEpoch);
    session.Disconnected += ()     => GD.Print("wire closed");
};

// 3. Mount the platform and exchange it for a session (login + gameplay start).
var session = await sdk.ConnectAsync(new SdkConfiguration {
    Platform = "douyin", AppId = "app-1", Tenant = "tenant-a",
    GameId = "game-1", Endpoint = "wss://gateway.example/ws", Role = "player",
}, new MyPlatformAdapter(), default);

// 4. Join a match, wait for the room, subscribe to its authoritative stream.
await session.JoinMatchAsync("standard", null, default);
var room = await session.AwaitRoomAsync(default);
var stream = await session.SubscribeAsync(room.RoomId, room.RoomEpoch, -1, -1, default);

// 5. Drive the stream from your own loop; the applier decides each frame.
var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
while (await stream.MoveNextAsync(default)) {
    var decision = applier.Decide(stream.Current);
    if (decision.Kind == SyncDecisionKind.ApplySnapshot || decision.Kind == SyncDecisionKind.ApplyDelta)
        GD.Print("state version ", applier.Status.StateVersion);
}
```

## Layout

| Path | Contents |
| --- | --- |
| `Runtime/` | The leaf: Envelope codec, transport seam + binding, session lifecycle, sync applier, wire shapes |
| `DESIGN.md` | Track evaluation (GDScript vs C# vs GDExtension) and the decision record |
| `Samples~/` | Godot 4 C# bridge + loop example (compile-removed; not part of the assembly) |

## Testing

```bash
dotnet test tests/Chorus.GodotSdk.Tests        # the Godot SDK's own lane
dotnet test tests/Chorus.Engine.Tests          # includes the CrossEngine golden set (EK-2)
```
