---
title: Architecture Design
---

<p align="center">
  English | <a href="architecture.zh.md">简体中文</a>
</p>

# Architecture Design

## Overall Components

```mermaid
flowchart LR
  subgraph Platform["抖音/快手等平台"]
    P1["弹幕/礼物/点赞事件"]
  end

  subgraph Chorus["Chorus Server"]
    Ingress["Platform Ingress (HTTP/Webhook)"]
    GW["Client Gateway (WebSocket)"]
    SM["SessionManager"]
    SA["SessionActor (单线程邮箱)"]
    GM["GameModule (权威逻辑)"]
  end

  subgraph Clients["主播客户端/直播伴侣"]
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
