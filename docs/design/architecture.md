---
title: Architecture Design
---


# Architecture Design

## Overall Components

```mermaid
flowchart LR
  subgraph Platform["Douyin/Kuaishou and other platforms"]
    P1["Danmaku/gift/like events"]
  end

  subgraph Chorus["Chorus Server"]
    Ingress["Platform Ingress (HTTP/Webhook)"]
    GW["Client Gateway (WebSocket)"]
    SM["SessionManager"]
    SA["SessionActor (single-threaded mailbox)"]
    GM["GameModule (authoritative logic)"]
  end

  subgraph Clients["Streamer client/Live Companion"]
    C1["Client A"]
    C2["Client B"]
  end

  P1 --> Ingress --> SA
  C1 <-->|hello/ack/snapshot| GW --> SM --> SA
  C2 <-->|hello/ack/snapshot| GW
  SA --> GM
  GM --> SA
```

## Core Principles (v1)

- Server-authoritative: the state advances on the server; the client only "aligns + renders"
- The server never pauses: slow endpoints catch up via forced snapshots, without holding back fast endpoints
- Backpressure: a cap on the number of events per tick + merging/downsampling, to avoid "frame explosions"
