# Built-in developer capabilities: leaderboards first

Target scope, 2026-10-29. A built-in capability includes its default provider, migrations, business API, client example and executable acceptance checks. Start with leaderboards; profile, inventory, settlement, session recovery, GM and observability retain their existing work items.

The redesign already has a settlement-transaction score projection and TopN reads, ordered by descending score then ascending player ID. That foundation is not a complete general leaderboard: it couples score to reward delta and lacks full period, subject, personal-rank and stable-pagination contracts.

## Target API and defaults

Provide RecordScoreEffect, GetTop, GetRank, GetAroundMe and GetEntry. These are proposed APIs, not an available SDK. Score changes derive from immutable committed server effects, identified by EffectId/GrantId. Retry, recovery and projection rebuild cannot add the same score twice or issue rewards again. Never trust client-reported scores or automatically turn every asset change into ranking points.

Keys include tenant, game, board, period and scope. Default subject is Player; Streamer and Faction are explicit alternatives. Begin with integer Sum and Max modes, validated bounds and explicit negative-score policy. Sort by score descending and stable subject ID ascending, producing unique ordinal ranks. Default period is AllTime; day/week/season periods specify timezone and fixed boundaries. Switching periods does not erase old boards. Default scope is the game; room/live-session scopes are explicit. Cross-platform identity merging is separate work.

Queries include TopN, personal rank and nearby entries, with bounded page size. A query version/cursor must bind pagination to a coherent read snapshot; expired snapshots require restarting the query. Final rewards reference a frozen immutable ranking version rather than repeated queries of a changing TopN. Late settlement has a declared period attribution. Query authorization restricts tenant, game, identity and exposed profile data.

GM adjustment/reset names the board, period and scope and includes expected version, operation ID, reason and audit evidence. Original scoring evidence remains available. Period cutoff, pending projection, freeze and archive are separate states.

## Process ownership and usable delivery

GameServer emits committed scoring intent. A business leaderboard/settlement module within ControlServer validates its source and selects board/period/scope; DataServer executes explicit idempotent reads and writes. StreamerServer and gateways expose authorized queries. Platform leaderboard uploads remain separate PlatformGateway tasks; internal projection success does not prove external success. No new leaderboard process is required initially.

Deliver a default PostgreSQL provider, migrations and one runnable example covering scoring, TopN, personal rank, duplicate settlement and period rollover with historical-board queries. Pending capabilities are tracked individually in TODO.
