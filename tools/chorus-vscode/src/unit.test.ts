import * as path from "node:path";
import { describe, expect, it } from "vitest";
import { buildSampleCommand } from "./flame.js";
import { panelHtml, PANEL_SCRIPT } from "./panelHtml.js";

describe("flame sample command", () => {
  it("builds a one-step collect-and-convert invocation", () => {
    const command = buildSampleCommand(4242, 30, "/tmp/chorus-flame");
    expect(command.file).toBe("dotnet-trace");
    expect(command.args).toEqual([
      "collect",
      "--pid",
      "4242",
      "--duration",
      "00:00:30",
      "--format",
      "speedscope",
      "--output",
      expect.stringMatching(/^\/tmp\/chorus-flame\/chorus-engine-4242-\d+\.speedscope\.json$/),
    ]);
    expect(command.output).toBe(command.args[command.args.length - 1]);
  });

  it("clamps a nonsense duration to a whole second", () => {
    const command = buildSampleCommand(1, 0, "/tmp");
    expect(command.args).toContain("00:00:01");
  });

  it("keeps the trace outside any workspace", () => {
    const command = buildSampleCommand(7, 30, path.join(path.sep, "home", "someone", ".tmp"));
    expect(command.output.startsWith(path.join(path.sep, "home", "someone", ".tmp"))).toBe(true);
    expect(command.output.endsWith(".speedscope.json")).toBe(true);
  });
});

describe("panel page", () => {
  it("binds the one inline script to the CSP nonce", () => {
    const html = panelHtml({ nonce: "abc123" });
    expect(html).toContain(`<script nonce="abc123">`);
    expect(html).toContain("acquireVsCodeApi");
    expect((html.match(/<script/g) ?? []).length).toBe(1);
  });

  it("renders the charts, the log toolbar, and the sessions table", () => {
    const html = panelHtml({ nonce: "n" });
    for (const id of ["cpuChart", "memChart", "filter", "level", "logs", "sessions", "drops"]) {
      expect(html).toContain(`id="${id}"`);
    }
  });

  it("speaks the one-way push protocol", () => {
    for (const kind of ["status", "metrics", "logs", "sessions"]) {
      expect(PANEL_SCRIPT).toContain(`m.t === "${kind}"`);
    }
  });

  it("reads camelCase session rows — the minimal-API face serializes with Web defaults", () => {
    expect(PANEL_SCRIPT).toContain("s.sessionId");
    expect(PANEL_SCRIPT).toContain("s.tickId");
    expect(PANEL_SCRIPT).toContain("c.lagTicks");
    expect(PANEL_SCRIPT).not.toContain("s.SessionId");
  });

  it("caps what the page keeps so a long attach cannot grow without bound", () => {
    expect(PANEL_SCRIPT).toContain("maxPoints: 300");
    expect(PANEL_SCRIPT).toContain("maxLogs: 2000");
  });
});
