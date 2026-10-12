module.exports = {
  title: "Chorus",
  description: "Chorus：直播弹幕/礼物驱动的互动对战游戏框架（服务器权威 Tick + 状态快照）",
  base: process.env.VUEPRESS_BASE || "/",
  themeConfig: {
    logo: "/logo.svg",
    repo: "cuihairu/chorus",
    docsDir: "docs",
    nav: [
      { text: "指南", link: "/guide/getting-started.html" },
      { text: "设计", link: "/design/architecture.html" },
      { text: "参考", link: "/reference/protocol.html" }
    ],
    sidebar: {
      "/guide/": [
        "/guide/getting-started.md",
        "/guide/split-mode.md",
        "/guide/load-testing.md",
        "/guide/game-module.md"
      ],
      "/design/": [
        "/design/architecture.md",
        "/design/frame-sync.md",
        "/design/frame-sync-best-practices.md",
        "/design/client-resync-state-machine.md",
        "/design/ipc.md",
        "/design/spec.md",
        "/design/legacy-real-server-analysis.md",
        "/design/review-2026-10-12-chatgpt.md",
        "/design/process-topology.md"
      ],
      "/reference/": [
        "/reference/protocol.md",
        "/reference/config.md",
        "/reference/operations.md",
        "/reference/observability.md",
        "/reference/platform-douyin.md",
        "/reference/platform-kuaishou.md",
        "/reference/tuning.md",
        "/reference/troubleshooting.md",
        "/reference/adapter-contract.md",
        "/reference/deployment.md",
        "/reference/versioning.md"
      ]
    }
  },
  plugins: ["mermaidjs"]
};
