import { defineConfig } from "vitepress";
import { withMermaid } from "vitepress-plugin-mermaid";

export default withMermaid(defineConfig({
  title: "Chorus",
  description: "An interactive battle game framework driven by live-stream danmaku/gifts",
  base: process.env.VITEPRESS_BASE || "/chorus/",
  head: [["link", { rel: "icon", href: "/logo.svg" }]],
  locales: {
    root: { label: "简体中文", lang: "zh-CN" }
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
      }
    }
  },
  mermaid: {}
}));
