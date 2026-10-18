
# Decision log (for review)

> Proxy decision-making is user-authorized (since 2026-10-12). Each entry carries background and
> impact; if a review changes direction, the rollback surface is noted here.

## 2026-10-12

### D1 Platform adapters move into the shared project `Chorus.Adapters.Platforms`

- **Background**: the `Ingress/*` trio (`AllowAllPlatformEventVerifier` / `EventDeduplicator` /
  `TestPlatformEventMapper`) existed once each in `Chorus.Server` and `Chorus.Gateway`
  (todo §8 P1 already listed "merge duplicated code").
- **Decision**: add `src/Chorus.Adapters.Platforms` (referencing `Chorus.Protocol` and
  `Chorus.Adapters.Abstractions`, neither of which references Engine/Server/Gateway); platform
  adapters and shared ingest parts get a single implementation, Server / Gateway switch to
  referencing it; the two duplicate copies are deleted.
- **Impact**: the old namespaces `Chorus.Server.Ingress` / `Chorus.Gateway.Ingress` are removed and
  tests updated. Platform-private logic stays out of core (external review §10). Rollback surface:
  restore the two directory copies.

### D2 Signature verification routes by platform: named verifier + router, no fallback = fail-closed

- **Background**: ingest originally had a single `IPlatformEventVerifier` singleton passing
  everything; per-platform verifiers needed a dispatch mechanism.
- **Decision**:
  - `IPlatformNamedEventVerifier` (bound to a platform name) registers into
    `PlatformEventVerifierRouter`;
  - platform name present → the matching verifier; absent → the dev fallback
    (`AllowAllPlatformEventVerifier`, turned into an explicit switch in phase-one item ③);
  - neither → **reject (fail-closed)** with a WARN.
- **Impact**: onboarding a new platform = register one named verifier + one mapper; routing is
  untouched.

### D3 Unverified parts of the two platforms' official specs: placeholder contracts + TODO, no guessed implementations

- **Background**: the Douyin docs site sits behind a login wall + anti-scraping, so the signature
  algorithm / event fields / push-channel shape are unverified (details in
  [platform-douyin.md §1](./reference/platform-douyin.md)); for Kuaishou we have the official
  "message encryption/decryption and signature verification" text, and the signature
  (sha1Hex(body+Token) / kwaisign) and AES envelope are implemented per the official spec, but the
  inner JSON of live events was not specified (details in
  [platform-kuaishou.md §1](./reference/platform-kuaishou.md)).
- **Decision**:
  - Douyin: verification uses HMAC-SHA256(appSecret, body) as a placeholder; the event envelope
    uses Chorus-defined placeholder fields;
  - Kuaishou: verification and envelope decryption follow the official algorithms; only the inner
    event fields use a placeholder contract (same shape as Douyin's, so both swap at the same point
    during integration);
  - placeholder parts are explicitly marked "pending integration" in code comments and the platform
    reference docs; swap points converge to a single file each (the verifier's ComputeSignature /
    the mapper's field extraction).
- **Impact**: the integration-time change surface = 1 verifier method + 1 mapper file + config
  header names per platform; pipeline, routing and test structure unchanged.

### D4 The two platforms' credentials come from configuration, not hard-coded

- **Decision**: `Ingress:Douyin:AppSecret`, `Ingress:Kuaishou:VerifyToken` /
  `Ingress:Kuaishou:MsgEncryptKey` (and header names) are injected from `IConfiguration`; a missing
  key → fail-closed.
- **Impact**: production secrets arrive via environment variables / a config center; no real
  secrets land in the repo.

### D5 Split the listener code first inside the monolith (platform-ingress / streamer-ws), do not split processes

- **Background**: [process-topology.md](./design/process-topology.md) settled on "roles split
  first, processes split later".
- **Decision**: add the shared project `Chorus.Gateway.Core` hosting the face policy components:
  - `platform-ingress` face: rate limiting (fixed window per source), request body cap (413),
    request timeout (504), per-platform verification, dedup — landing in both hosts'
    `POST /ingress/{platform}` handlers + the `/faces` counters endpoint;
  - `streamer-ws` face: auth, ACK rate limiting, message size cap, landing in the `/ws` handler
    (shipped 2026-10-12, see D6);
  - the two faces get separate config paths (`Faces:PlatformIngress:*` / `Faces:StreamerWs:*`),
    per-face log categories (`Chorus.StreamerWs`; the ingress face has no separate log category
    yet), and counters exposed per face via `GET /faces`.
- **Impact**: the monolith stays the default deployment shape; physical splitting waits for the
  trigger conditions (worker CPU>60% or hundreds of rooms per machine), see topology doc §4.

### D6 Streamer WS auth fail-closed + Ingress:AllowAll off by default (demo opts in via the Development config)

- **Background**: review items P0-4 (production must reject unverified ingress by default) and
  P1-10 (client message bounds/auth flow); previously `/ws` accepted anyone with no credentials,
  and `AllowAllPlatformEventVerifier` was registered unconditionally, i.e. always pass.
- **Decision**:
  - `/ws` hello carries a `token`, verified server-side (`Faces:StreamerWs:AuthTokens`, supports
    `token@unixMsExpiry`); missing/unknown/expired → reply error then close with `PolicyViolation`;
    **no token configured and anonymous not enabled → reject all (fail-closed)**;
  - only `Ingress:AllowAll` (default `false`) registers the unsigned-verification fallback;
    platforms without a real verifier 401 outright under production config;
  - message body cap (default 64KiB, over-limit `MessageTooBig` closes the connection) and ACK rate
    limit (default 600/connection/minute, over-limit dropped and counted);
  - **local demo impact absorbed**: appsettings.Development.json (Server/Gateway) sets
    `Ingress:AllowAll=true` + `Faces:StreamerWs:AllowAnonymous=true` explicitly, marked dev-only;
    production config carries neither, and startup logs a loud warning for both.
- **Impact**: plain `dotnet run` (Development) demo behavior unchanged; production/test
  environments reject everything by default and need a token or callback verification keys.
  Rollback surface: restore unconditional AllowAll registration + drop the hello check.

## 2026-10-16

### D7 UDS socket file lifecycle is self-managed (proxy-decided)

- **Background**: a rolling-restart race — since .NET 6, `Socket.Dispose` unconditionally unlinks
  the path bound with `UnixDomainSocketEndPoint` (dotnet/runtime#52103, no opt-out). If the old
  process disposes while the new process has already delete-bound the same path, what gets
  unlinked is the new process's socket file entry: the kernel socket lives on but the path is gone,
  so later connects all get ENOENT (load tests used to work around it with manual
  `rm -f /tmp/chorus/*.sock`).
- **Decision**: the server bind switches to a custom `RawUnixEndPoint` (sockaddr layout copied from
  the BCL, outside its deletion tracking), so path deletion returns to the bus; on close, the
  delete is guarded by bind identity (Unix `CreationTimeUtc` = st_ctime, unchanged after bind) —
  unknown identity or a file that changed owner is never deleted, and leftovers from a crashed
  process are cleaned by the next `StartServer`'s delete-then-bind. The delete hangs off
  `DisposeAsync`, not the accept-loop tail (a `Task.Run` with a cancelled token may never be
  scheduled, and a guard at the loop tail would be skipped entirely).
  Design details: [docs/design/ipc.md](./design/ipc.md) "socket file lifecycle".
- **Impact**: wire protocol, channel names and config all unchanged; the client `ConnectAsync`
  still uses the BCL endpoint (never binds, never tracked). Rollback surface: switch the bind
  endpoint back to `UnixDomainSocketEndPoint` and restore the original `File.Delete` to return to
  BCL management (the race returns).

### D8 Pipelined frame reads + merged writes (proxy-decided)

- **Background**: in split mode the eng→gw single socket saturated at a 21.5k msg/s ceiling under
  800-room churn, ~3.7% publish failures (todo.md §10 review). The bottleneck was I/O calls per
  message, not bandwidth: the write side did two `WriteAsync` (header + payload) + `Flush` per
  frame; the read side did two independent `ReadAsync` per frame (header + payload); and JSON
  serialization sat inside the `_clientLock` critical section, so all publishers queued behind it.
  The snapshot path already dedups broadcasts (one message per session per tick), leaving no
  coarser merge point.
- **Decision**:
  - read side gains `FrameReader` (buffered, parses on frame boundaries): one `ReadAsync` can
    yield several frames; a frame larger than the buffer is read straight into the destination
    array, so the buffer never grows with frame size. Shared by the three stream transports
    (UDS/TCP/NamedPipe);
  - write side: small frames (≤16KiB) merge header+payload into one `WriteAsync` (the receiver is
    a byte stream and cannot see write boundaries, so framing is unaffected); large frames keep two
    writes, avoiding copying a whole payload to save one syscall;
  - the UDS publisher moves `JsonEnvelopeCodec.Serialize` out of the `_clientLock` critical section.
- **Impact**: wire bytes unchanged, failure semantics unchanged (clean EOF returns null, truncated
  header/payload throws IOException, invalid lengths rejected) — zero awareness on the peer;
  TCP/NamedPipe keep serialization inside the lock (this round only optimized the measured UDS
  bottleneck; no spreading the change surface without a measured need). Rollback surface: swap the
  three transports' `FrameReader` back to `LengthPrefixedStream.ReadFrameAsync` and the merged
  write back to two writes. Multi-socket sharding and a write-coalescing pump (batched frame write
  queue) wait for the gate re-measurement.

### D9 Gate re-measurement baseline taken: ② (multi-socket sharding / write-coalescing pump) stays deferred (proxy-decided, 2026-10-16)

- **Background**: when D8 landed (pipelined reads + merged writes + serialization out of the
  lock), "per-session multi-socket sharding" and the "write-coalescing pump" were left pending a
  gate re-measurement to set parameters — a missing baseline, not an external condition. This
  round supplies that baseline: same topology (EngineHost + platform-gw + streamer-gw,
  `Ipc:GatewayIds=platform-gw,streamer-gw`, `Session:MaxSessions=1000`), same parameters, one run
  each on the pre-D8 tree `e74291c` and the post-D8 HEAD (both trees built with
  `dotnet build -c Debug -o` into separate directories, to avoid switching the work tree
  mid-run). The primary evidence is the churn-free group (800 rooms × 1 client,
  `--disconnect-every 0`, 128 producers, 60s) whose workloads match (delivered 107,794 vs 108,085,
  0.3% apart); the churn group (800 rooms × 3 clients = 2,400 WS) had mismatched driver-side
  workloads (delivered 53,105 vs 32,657) and serves only as corroboration.
- **Measurements (churn-free primary evidence)**: `ipc_publish_failures` 4,324→1,425 (−67%;
  normalized by `snapshots_sent`, 0.69%→0.21%), `snapshots_sent` +11.0%, engine
  `client_queue_dropped` −97%, gateway `ws_snapshot_dropped` −95%, engine peak CPU 30.32→27.08
  (−10.7%, 14 cores 4.25→3.79), unscheduled reconnects 4,775→1,678 (−65%), zero event loss on both
  sides (zero 503/429/401), `maxLagTicks` 13 unchanged on both sides, error categories identical.
  **The only regression**: `tick_overruns` (`TickTimeoutMs=250`) 13→38 (+2.9×).
- **Regression located structurally** (no timing instrumentation): the in-tick snapshot broadcast
  is awaited (`SessionActor.cs:636/671/703`), and via `EngineBusSessionClient.SendSnapshotAsync`
  → UDS `PublishAsync` it serially writes a single socket under a single `_clientLock`; D8 made
  per-frame I/O cheaper, delivery rose 11%, and the write-blocking exposure on the same socket
  rose with it (same direction in the churn group: 1→15). So the residual bottleneck is the pair
  "in-tick awaited publish" + "single-socket serial fan-out" — not bandwidth, not I/O calls per
  frame. The merged-write buffer's per-frame allocation (`WriteFrameAsync` does `new byte[4+n]`
  for ≤16KiB frames; measured snapshot mean 2,748 B) is not a source of 250ms-class stalls.
- **Decision**: ② stays deferred, with the reason updated from "missing baseline" to "baseline
  taken, trigger line not reached" — the three peak readings 21.67–30.32% (3.0–4.25 cores) keep
  2.0–2.8× headroom against the 60% trigger line (8.4 cores); and a write-coalescing pump would
  turn `PublishAsync`'s failure semantics from synchronous to asynchronous
  (`snapshots_sent_total` from "written" to "enqueued", the `SafePublishAsync` /
  `WelcomePublisher` trade-offs need reordering, the welcome latency budget changes), while its
  target workload (30k rps) is still capped by the single-process LoadTool driver (churn-free
  only 1,797 rps this round; ~862 rps ceiling under churn).
- **Trigger conditions (made explicit)**: either reopens ② — engine peak CPU > 60% (8.4 cores);
  or the LoadTool driver rework (async sends / multi-process driving) lands, can push the
  800-room churn group's delivery to the target rate, and reproduces the `tick_overruns` rise with
  delivery. Until either holds, no critical-path semantics changes for unreachable loads.
- **Methodology note**: driver throughput fluctuates with machine load (churn-free 1,797 rps this
  round vs 4,706 rps recorded in R1015D); absolute rates are not comparable across rounds;
  cross-tree comparisons must use same-round A/B normalized counters (per `snapshots_sent`, per
  reconnect).
