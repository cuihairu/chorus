---
title: Bots & Pressure
---


# Bots & Pressure (Console Fleet Page)

The console's **Bots** page drives the simulator fleet and the pressure driver from the panel. It has three groups of faces:

- **Scenarios** — bot scripts as TOML documents, stored as plain records in the control plane's database (they survive a control-host restart). The start of a run parses the script up front, so a bad script answers at the request, not mid-run.
- **Runs** — one scenario played against the real chain: mount over the streamer gateway's gRPC face, play the schedule over the ingress face, then reconcile deliveries and scores against the data plane. A run with deviations names itself `Deviated` and lists every deviation; an empty list is the pass.
- **Pressure runs** — the load tool's own `IngressDriver` running in-process in the control host, driven from the panel instead of the CLI. No second driver semantics: the same `RatePacer` absolute schedule, the same per-outcome counters (`accepted` / `duplicated` / `unauthorized` / `rateLimited` / `other`), the same signed-or-unsigned request shapes as [Load Testing](../guide/load-testing.md).

Related docs:

- [Load Testing](../guide/load-testing.md)
- [Observability](../reference/observability.md)

## Pressure runs

One live pressure run at a time — a second start while one is live answers `409`, because a reading is never clean with two sources writing over each other. Event ids draw from a per-run prefix, so one run's dedup keys never collide with the next.

```bash
# Start a run against the ingress face (operator/admin "operate" policy).
curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"baseUrl":"https://platform-gateway:443","platform":"douyin","eventsPerSecond":120,"sessions":8,"producerConnections":4,"durationSeconds":30}' \
  https://control-host:443/api/pressure/runs

# Watch it; every record carries the driver's counters plus target vs achieved rate.
curl -H "Authorization: Bearer $TOKEN" https://control-host:443/api/pressure/runs/pressure-run-1

# Stop it before the window elapses; the run names itself Stopped, never Completed.
curl -X POST -H "Authorization: Bearer $TOKEN" https://control-host:443/api/pressure/runs/pressure-run-1/stop
```

Bad requests answer at the request: a relative or non-HTTP base URL, a zero rate, a missing root CA file are all `400` before anything spawns. An unknown run id answers `404`.

To drive an HTTPS ingress face whose certificate chains to your own root, pass `rootCaPemPath` — the run then trusts exactly that root (the same custom-root rule the internal clients dial with), nothing else.

## The one-click flame graph

Add `profileSeconds` to a pressure run and the capture comes with it:

```bash
curl -X POST ... -d '{"...":"...","durationSeconds":30,"profileSeconds":30}' \
  https://control-host:443/api/pressure/runs
```

- The capture window is capped at the run's duration — a trace cannot outlive the run it profiles.
- By default it profiles the control host's own process (where the drivers run). Name the gateway's pid with `profileProcessId` to profile the gateway instead.
- It needs `dotnet-trace` installed where the control host runs: `dotnet tool install -g dotnet-trace`.
- When the window closes, the nettrace converts to speedscope; the run record carries both paths, and the panel ranks the top ten frames by **self time share** — every sample's leaf frame owns that sample's weight, so the top leaves are where the wall clock went.

Open the speedscope path at [speedscope.app](https://speedscope.app) for the full graph; the panel's ranked list is the quick "which function is eating the run" answer.

The capture is best-effort to its bones: a missing tool, a failed collect, a failed conversion, or an unreadable speedscope file is guidance in the host's stderr and empty hot frames on the record — never a failed pressure run.

## Honest boundary

The ingress and delivery statistics faces are **count families only** — accepted, duplicated, rate-limited, and so on. The panel does not carry latency percentiles, so there is no P99 histogram to point at a slow window; those live in the engine's own metrics plane and this panel deliberately does not duplicate it. What BOTS-3 gives the operator is the pairing of "how many, at what rate, against what target" (the pressure counters) with "in which functions the wall clock went" (the flame graph) — correlating a slow window with the matching capture stays operator work.
