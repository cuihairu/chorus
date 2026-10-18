module.exports = {
  title: "Chorus",
  description: "An interactive battle game framework driven by live-stream danmaku/gifts",
  locales: {
    "/": { lang: "en-US", title: "Chorus" },
    "/zh/": { lang: "zh-CN", title: "Chorus", description: "Chorus：直播弹幕/礼物驱动的互动对战游戏框架（服务器权威 Tick + 状态快照）" }
  },
  base: process.env.VUEPRESS_BASE || "/",
  themeConfig: {
    logo: "/logo.svg",
    repo: "cuihairu/chorus",
    docsDir: "docs",
    locales: {
      "/": {
        label: "English",
        selectText: "Languages",
        nav: [
          { text: "Guides", link: "/guide/getting-started.html" },
          { text: "Architecture", link: "/architecture.html" },
          { text: "Design", link: "/design/architecture.html" },
          { text: "Reference", link: "/reference/protocol.html" }
        ],
        sidebar: {
          "/": ["/architecture.md", "/decisions.md"],
          "/guide/": [
            "/guide/getting-started.md",
            "/guide/split-mode.md",
            "/guide/load-testing.md",
            "/guide/battle.md",
            "/guide/matchmaking.md",
            "/guide/leaderboard.md",
            "/guide/game-module.md",
            "/guide/quickstart-frame-sync.md"
          ],
          "/design/": [
            "/design/architecture.md",
            "/design/architecture-overview.md",
            "/design/frame-sync.md",
            "/design/frame-sync-best-practices.md",
            "/design/client-resync-state-machine.md",
            "/design/ipc.md",
            "/design/shared-memory-transport.md",
            "/design/spec.md",
            "/design/legacy-real-server-analysis.md",
            "/design/review-2026-10-12-chatgpt.md",
            "/design/process-topology.md",
            "/design/snapshot-restore.md",
            "/design/bilibili-danmaku-adapter.md",
            "/design/overall-architecture.md",
            "/audit-doc-consistency.md"
          ],
          "/reference/": [
            "/reference/protocol.md",
            "/reference/config.md",
            "/reference/operations.md",
            "/reference/observability.md",
            "/reference/platform-douyin.md",
            "/reference/platform-douyin-mini-game.md",
            "/reference/platform-kuaishou.md",
            "/reference/platform-kuaishou-mini-game.md",
            "/reference/platform-bilibili.md",
            "/reference/tuning.md",
            "/reference/troubleshooting.md",
            "/reference/adapter-contract.md",
            "/reference/deployment.md",
            "/reference/versioning.md",
            "/reference/api.md"
          ]
        }
      },
      "/zh/": {
        label: "简体中文",
        selectText: "语言",
        nav: [
          { text: "指南", link: "/zh/guide/getting-started.html" },
          { text: "架构", link: "/zh/architecture.html" },
          { text: "设计", link: "/zh/design/architecture.html" },
          { text: "参考", link: "/zh/reference/protocol.html" }
        ],
        sidebar: {
          "/zh/": ["/zh/architecture.md", "/zh/decisions.md"],
          "/zh/guide/": [
            "/zh/guide/getting-started.md",
            "/zh/guide/split-mode.md",
            "/zh/guide/load-testing.md",
            "/zh/guide/battle.md",
            "/zh/guide/matchmaking.md",
            "/zh/guide/leaderboard.md",
            "/zh/guide/game-module.md",
            "/zh/guide/quickstart-frame-sync.md"
          ],
          "/zh/design/": [
            "/zh/design/architecture.md",
            "/zh/design/architecture-overview.md",
            "/zh/design/frame-sync.md",
            "/zh/design/frame-sync-best-practices.md",
            "/zh/design/client-resync-state-machine.md",
            "/zh/design/ipc.md",
            "/zh/design/shared-memory-transport.md",
            "/zh/design/spec.md",
            "/zh/design/legacy-real-server-analysis.md",
            "/zh/design/review-2026-10-12-chatgpt.md",
            "/zh/design/process-topology.md",
            "/zh/design/snapshot-restore.md",
            "/zh/design/bilibili-danmaku-adapter.md",
            "/zh/design/overall-architecture.md",
            "/zh/audit-doc-consistency.md"
          ],
          "/zh/reference/": [
            "/zh/reference/protocol.md",
            "/zh/reference/config.md",
            "/zh/reference/operations.md",
            "/zh/reference/observability.md",
            "/zh/reference/platform-douyin.md",
            "/zh/reference/platform-douyin-mini-game.md",
            "/zh/reference/platform-kuaishou.md",
            "/zh/reference/platform-kuaishou-mini-game.md",
            "/zh/reference/platform-bilibili.md",
            "/zh/reference/tuning.md",
            "/zh/reference/troubleshooting.md",
            "/zh/reference/adapter-contract.md",
            "/zh/reference/deployment.md",
            "/zh/reference/versioning.md",
            "/zh/reference/api.md"
          ]
        }
      }
    }
  },
  plugins: ["mermaidjs"]
};
