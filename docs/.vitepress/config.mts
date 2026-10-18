import { defineConfig } from "vitepress";
import { withMermaid } from "vitepress-plugin-mermaid";

export default withMermaid(defineConfig({
  title: "Chorus",
  description: "An interactive battle game framework driven by live-stream danmaku/gifts",
  base: process.env.VITEPRESS_BASE || "/chorus/",
  head: [["link", { rel: "icon", href: "/logo.svg" }]],
  locales: {
    root: {
      label: "English",
      lang: "en-US"
    },
    zh: {
      label: "简体中文",
      lang: "zh-CN"
    }
  },
  themeConfig: {
    logo: "/logo.svg",
    outline: "deep",
    socialLinks: [
      { icon: "github", link: "https://github.com/cuihairu/chorus" }
    ],
    locales: {
      root: {
        selectText: "Languages",
        nav: [
          { text: "Guides", link: "/guide/getting-started" },
          { text: "Architecture", link: "/architecture" },
          { text: "Design", link: "/design/architecture" },
          { text: "Reference", link: "/reference/protocol" }
        ],
        sidebar: {
          "/": ["/architecture", "/decisions"],
          "/guide/": [
            "/guide/getting-started",
            "/guide/split-mode",
            "/guide/load-testing",
            "/guide/battle",
            "/guide/matchmaking",
            "/guide/leaderboard",
            "/guide/game-module",
            "/guide/quickstart-frame-sync"
          ],
          "/design/": [
            "/design/architecture",
            "/design/architecture-overview",
            "/design/frame-sync",
            "/design/frame-sync-best-practices",
            "/design/client-resync-state-machine",
            "/design/ipc",
            "/design/shared-memory-transport",
            "/design/spec",
            "/design/legacy-real-server-analysis",
            "/design/review-2026-10-12-chatgpt",
            "/design/process-topology",
            "/design/snapshot-restore",
            "/design/bilibili-danmaku-adapter",
            "/design/overall-architecture",
            "/audit-doc-consistency"
          ],
          "/reference/": [
            "/reference/protocol",
            "/reference/config",
            "/reference/operations",
            "/reference/observability",
            "/reference/platform-douyin",
            "/reference/platform-douyin-mini-game",
            "/reference/platform-kuaishou",
            "/reference/platform-kuaishou-mini-game",
            "/reference/platform-bilibili",
            "/reference/tuning",
            "/reference/troubleshooting",
            "/reference/adapter-contract",
            "/reference/deployment",
            "/reference/versioning",
            "/reference/api"
          ]
        }
      },
      zh: {
        selectText: "语言",
        nav: [
          { text: "指南", link: "/zh/guide/getting-started" },
          { text: "架构", link: "/zh/architecture" },
          { text: "设计", link: "/zh/design/architecture" },
          { text: "参考", link: "/zh/reference/protocol" }
        ],
        sidebar: {
          "/zh/": ["/zh/architecture", "/zh/decisions"],
          "/zh/guide/": [
            "/zh/guide/getting-started",
            "/zh/guide/split-mode",
            "/zh/guide/load-testing",
            "/zh/guide/battle",
            "/zh/guide/matchmaking",
            "/zh/guide/leaderboard",
            "/zh/guide/game-module",
            "/zh/guide/quickstart-frame-sync"
          ],
          "/zh/design/": [
            "/zh/design/architecture",
            "/zh/design/architecture-overview",
            "/zh/design/frame-sync",
            "/zh/design/frame-sync-best-practices",
            "/zh/design/client-resync-state-machine",
            "/zh/design/ipc",
            "/zh/design/shared-memory-transport",
            "/zh/design/spec",
            "/zh/design/legacy-real-server-analysis",
            "/zh/design/review-2026-10-12-chatgpt",
            "/zh/design/process-topology",
            "/zh/design/snapshot-restore",
            "/zh/design/bilibili-danmaku-adapter",
            "/zh/design/overall-architecture",
            "/zh/audit-doc-consistency"
          ],
          "/zh/reference/": [
            "/zh/reference/protocol",
            "/zh/reference/config",
            "/zh/reference/operations",
            "/zh/reference/observability",
            "/zh/reference/platform-douyin",
            "/zh/reference/platform-douyin-mini-game",
            "/zh/reference/platform-kuaishou",
            "/zh/reference/platform-kuaishou-mini-game",
            "/zh/reference/platform-bilibili",
            "/zh/reference/tuning",
            "/zh/reference/troubleshooting",
            "/zh/reference/adapter-contract",
            "/zh/reference/deployment",
            "/zh/reference/versioning",
            "/zh/reference/api"
          ]
        }
      }
    }
  },
  mermaid: {}
}));
