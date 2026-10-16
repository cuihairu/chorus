---
title: Overall architecture and code agent implementation spec
---

<p align="center">
  English | <a href="overall-architecture.zh.md">简体中文</a>
</p>

# Overall architecture and code agent implementation spec

> **This document has no English translation yet.** The Chinese original is 1,478 lines
> (architecture design, code-audit baseline, 80 audit items, dated 2026-10-13); a
> low-quality machine translation would be worse than none, so it is left untranslated on purpose.
> The authoritative version is the
> [整体架构与 Code Agent 实施规范](./overall-architecture.zh.md).

For navigation only, the Chinese document's section structure (translated headings):

1. Project positioning and architecture goals
2. Overall logical architecture diagram — including implementation-status constraints on the modules in the diagram
3. Core domain objects and relationship diagram — including object semantics
4. Platform account unique binding and dual-entry routing
5. Room lifecycle and Worker scheduling
6. Matchmaking, real participants and bot architecture — including bot rules
7. Frame sync and authoritative Tick architecture — Tick semantics, state sync, relationship to the current `SessionActor`
8. Async player data and persistence architecture — reliability levels, suggested `DataCommand` contract, key constraints
9. Leaderboard subsystem — leaderboard types to be defined, authoritative data rules, query/update separation
10. Dual adapter architecture for live-stream and mini-game platforms — live adapter, mini-game adapter, candidate platforms and priorities
11. Room migration and failure recovery — guarantees, relationship to existing code
12. Process deployment topology
13. Observability and operations management
14. Existing code that must be audited
15. Phased implementation order — phase 0: architecture audit; phase 1: unify room semantics and routing; …
