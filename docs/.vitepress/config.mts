import { defineConfig } from "vitepress";
import { withMermaid } from "vitepress-plugin-mermaid";

// 侧栏/导航条目（zh locale 全部挂 /zh/ 前缀，与 root=英文 镜像）。
const guideLinks = [
  "/guide/getting-started",
  "/guide/split-mode",
  "/guide/independent-gateways",
  "/guide/load-testing",
  "/guide/battle",
  "/guide/matchmaking",
  "/guide/leaderboard",
  "/guide/game-module",
  "/guide/quickstart-frame-sync",
];
const designLinks = [
  "/design/room-recovery",
  "/design/authoritative-sync",
  "/design/developer-capabilities",
  "/design/gameserver-threading-current",
  "/design/lifecycle-flows",
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
  "/audit-doc-consistency",
];
const referenceLinks = [
  "/reference/glossary",
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
  "/reference/api",
];

const prefix = (links: string[], p: string) =>
  links.map((l) => p + l);

export default withMermaid(defineConfig({
  title: "Chorus",
  description: "An interactive battle game framework driven by live-stream danmaku/gifts",
  base: process.env.VITEPRESS_BASE || "/chorus/",
  head: [["link", { rel: "icon", href: "/logo.svg" }]],
  locales: {
    root: { label: "English", lang: "en-US" },
    zh: { label: "简体中文", lang: "zh-CN", link: "/zh/" },
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
          "/architecture/": ["/architecture/architecture", "/architecture/redesign-contracts", "/architecture/redesign-operations", "/architecture/redesign-decisions", "/architecture/redesign-acceptance"],
          "/guide/": guideLinks,
          "/design/": designLinks,
          "/reference/": referenceLinks,
        }
      },
      zh: {
        label: "简体中文",
        selectText: "Languages",
        nav: [
          { text: "指南", link: "/zh/guide/getting-started" },
          { text: "架构", link: "/zh/architecture" },
          { text: "设计", link: "/zh/design/architecture" },
          { text: "重设计方案", link: "/architecture/architecture" },
          { text: "参考", link: "/zh/reference/protocol" }
        ],
        sidebar: {
          "/zh/": ["/zh/architecture", "/zh/decisions"],
          "/zh/guide/": prefix(guideLinks, "/zh"),
          "/zh/design/": prefix(designLinks, "/zh"),
          "/zh/reference/": prefix(referenceLinks, "/zh"),
        }
      }
    }
  },
  mermaid: {},
  // esbuild 0.28 (安全覆盖) 不再降级解构到 es2015 目标，构建目标抬到 es2022。
  vite: { build: { target: "es2022" } }
}));
