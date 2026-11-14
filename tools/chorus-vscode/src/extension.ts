// Chorus (IDE-EXT): attach to a local engine's optional devtools channel and
// watch it — status bar link state, a panel with CPU / working-set curves,
// the engine log stream with filter, the session snapshot, and a flame
// graph sample entry. Read-only everywhere: the extension never mutates the
// engine; the devtools plane is push-only telemetry.
import * as vscode from "vscode";
import { AttachSession, DevMetricsSnapshot } from "@chorus/devcore";
import { resolveEndpoint, resolveEngineUrl } from "./engineConfig.js";
import { ChorusPanel } from "./panel.js";
import { buildSampleCommand, requireTool, runSample, samplablePid, sampleDirectory } from "./flame.js";

interface AttachState {
  session: AttachSession | null;
  lastMetrics: DevMetricsSnapshot | null;
}

// The live session, module-scoped: deactivate must hang up the socket even
// though the session is not a vscode.Disposable in context.subscriptions.
const state: AttachState = { session: null, lastMetrics: null };

export function activate(context: vscode.ExtensionContext): void {
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  status.command = "chorus.openPanel";

  const renderStatus = (): void => {
    if (state.session === null) {
      status.text = "$(circle-slash) Chorus";
      status.tooltip = "Detached. Run 'Chorus: Attach to Engine' (the engine needs --devtools).";
      return;
    }
    const { link } = state.session.status;
    if (link === "ready") {
      const cpu = state.lastMetrics?.cpuPercent;
      status.text = cpu === undefined ? "$(pulse) Chorus" : `$(pulse) Chorus ${cpu.toFixed(1)}%`;
      const pid = state.lastMetrics?.processId;
      status.tooltip = `Attached (link ready${pid === undefined ? "" : `, engine pid ${pid}`}).`;
    } else if (link === "backoff" || link === "connecting") {
      status.text = "$(sync~spin) Chorus";
      status.tooltip = `Reaching for the engine's devtools endpoint (${link}) — is the host running with --devtools?`;
    } else {
      status.text = "$(circle-slash) Chorus";
      status.tooltip = `Link ${link}.`;
    }
  };

  const panelFor = (): ChorusPanel | null => ChorusPanel.instance;

  /** The panel's link/source line — pushed only when a state changes
   * (link-state/source-state fire per transition; per-log posting would
   * itself be an unbatched stream). */
  const pushStatus = (): void => {
    const panel = ChorusPanel.instance;
    if (panel === null || state.session === null) return;
    const { link, metrics, log } = state.session.status;
    panel.postStatus({ link, metrics, log });
  };

  const attach = async (): Promise<AttachSession> => {
    if (state.session) return state.session;
    const session = new AttachSession({ transport: { endpoint: resolveEndpoint() } });
    // The session owns the source state machine (off/waiting/up); the shell
    // only mirrors it.
    session.on("link-state", () => {
      renderStatus();
      // The telemetry callbacks go quiet across a broken link — the panel
      // would keep its last "ready/up" forever. Push the transition itself.
      pushStatus();
    });
    // setSource guards equality, so this fires per transition only: the
    // waiting→up first frames and the off fallbacks.
    session.on("source-state", () => pushStatus());
    session.on("metrics", (snapshot) => {
      state.lastMetrics = snapshot;
      renderStatus();
      panelFor()?.postMetrics(snapshot, session.stats);
    });
    session.on("log", (entry) => panelFor()?.postLog(entry));
    session.on("backpressure", (depth) => panelFor()?.postBackpressure(depth));
    session.on("error", () => {
      // Quiet by design: the status bar carries the failure, the reconnect
      // loop keeps trying, and an offline engine is a normal state here.
    });
    state.session = session;
    renderStatus();
    await session.attach().catch(() => {
      // The transport keeps retrying; the status bar shows the chase.
    });
    renderStatus();
    return session;
  };

  const detach = (): void => {
    state.session?.detach();
    state.session = null;
    state.lastMetrics = null;
    renderStatus();
    // The session is gone, so no callback will ever post again — tell the
    // panel directly, or it shows the last live state indefinitely.
    ChorusPanel.instance?.postStatus({ link: "closed", metrics: "off", log: "off" });
  };

  const openPanel = async (): Promise<void> => {
    await attach();
    const panel = ChorusPanel.show(context);
    pushStatus();
    panel.pollSessions(resolveEngineUrl());
  };

  const sampleFlameGraph = async (): Promise<void> => {
    // Readiness is checked twice: before the tool probe and again after —
    // the probe awaits, and a link that dies in between must not leave the
    // sample aimed at the previous engine process.
    let pid = samplablePid(state.session?.status ?? null, state.lastMetrics?.processId);
    if (pid === null) {
      void vscode.window.showErrorMessage(
        "No live engine metrics — attach first and wait for the link (Chorus: Attach to Engine).",
      );
      return;
    }
    try {
      await requireTool("dotnet-trace");
    } catch (error) {
      void vscode.window.showErrorMessage((error as Error).message);
      return;
    }
    pid = samplablePid(state.session?.status ?? null, state.lastMetrics?.processId);
    if (pid === null) {
      void vscode.window.showErrorMessage("The engine link dropped — reattach before sampling.");
      return;
    }
    const command = buildSampleCommand(pid, 30, sampleDirectory());
    void vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: `Chorus: sampling pid ${pid} for 30s…`, cancellable: false },
      async () => {
        try {
          const output = await runSample(command);
          void vscode.commands.executeCommand("revealInExplorer", vscode.Uri.file(output));
          void vscode.env.openExternal(vscode.Uri.parse("https://speedscope.app"));
          void vscode.window.showInformationMessage(`Flame graph written: ${output}`);
        } catch (error) {
          void vscode.window.showErrorMessage(`Flame graph sample failed: ${(error as Error).message}`);
        }
      },
    );
  };

  context.subscriptions.push(
    status,
    vscode.commands.registerCommand("chorus.attach", async () => {
      await attach();
    }),
    vscode.commands.registerCommand("chorus.detach", detach),
    vscode.commands.registerCommand("chorus.openPanel", openPanel),
    vscode.commands.registerCommand("chorus.sampleFlameGraph", sampleFlameGraph),
  );
  status.show();
  renderStatus();

  // Attach quietly on activation: an engine with --devtools comes alive in
  // the status bar without a command; an absent one stays a quiet
  // circle-slash while the reconnect loop chases it.
  void attach();
}

export function deactivate(): void {
  // The socket is the OS-level resource: hang up, then dispose the panel —
  // its onDidDispose stops the /sessions poll with it. (context.subscriptions
  // — status bar, commands — VS Code disposes on its own.)
  state.session?.detach();
  state.session = null;
  state.lastMetrics = null;
  ChorusPanel.forget();
}
