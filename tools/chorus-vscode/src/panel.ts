// The Chorus panel controller (IDE-EXT): one webview, singleton per window.
// The extension owns the attach session and pushes state in — metrics
// snapshots on the engine's cadence, log entries batched every 500 ms so a
// startup burst does not storm postMessage, and the /sessions snapshot on a
// poll. The webview does its own filtering; nothing round-trips.
import * as vscode from "vscode";
import { DevLogEntry, DevMetricsSnapshot, QueueDepth, TransportStats } from "@chorus/devcore";
import { panelHtml } from "./panelHtml.js";

export interface SessionRow {
  sessionId: string;
  tickId: number;
  clients?: { lagTicks?: number }[];
  mailboxDepth?: number;
  totalDroppedEvents?: number;
  lastTickProcessMs?: number;
}

const LOG_FLUSH_MS = 500;
const SESSIONS_POLL_MS = 5000;
const MAX_BATCHED_LOGS = 500;

export class ChorusPanel {
  private static current: ChorusPanel | null = null;

  /** The open panel, or null. */
  static get instance(): ChorusPanel | null {
    return this.current;
  }

  /** Opens (or reveals) the singleton panel for this context. */
  static show(context: vscode.ExtensionContext): ChorusPanel {
    if (this.current) {
      this.current.reveal();
      return this.current;
    }
    this.current = new ChorusPanel(context);
    return this.current;
  }

  /** Closes the panel (its onDidDispose stops the polls) and forgets it so
   * the next show() builds a fresh one. Idempotent. */
  static forget(): void {
    const panel = this.current;
    this.current = null;
    panel?.dispose();
  }

  /** Disposes the webview; the onDidDispose hook stops the timers. */
  dispose(): void {
    this.panel.dispose();
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly pendingLogs: DevLogEntry[] = [];
  private logTimer: NodeJS.Timeout | null = null;
  private sessionsTimer: NodeJS.Timeout | null = null;
  private lastStats: TransportStats | null = null;

  private constructor(context: vscode.ExtensionContext) {
    this.panel = vscode.window.createWebviewPanel("chorus.panel", "Chorus engine", vscode.ViewColumn.Beside, {
      enableScripts: true,
    });
    this.panel.webview.html = panelHtml({ nonce: ChorusPanel.nonce() });
    this.panel.onDidDispose(
      () => {
        this.stopTimers();
        ChorusPanel.forget();
      },
      null,
      context.subscriptions,
    );
  }

  private reveal(): void {
    void this.panel.reveal();
  }

  private static nonce(): string {
    return Array.from({ length: 16 }, () => Math.floor(Math.random() * 16).toString(16)).join("");
  }

  /** Re-renders the page (a kept panel survives theme changes badly). */
  refreshHtml(): void {
    this.panel.webview.html = panelHtml({ nonce: ChorusPanel.nonce() });
  }

  postStatus(status: { link: string; metrics: string; log: string }): void {
    void this.panel.webview.postMessage({ t: "status", ...status });
  }

  postMetrics(snapshot: DevMetricsSnapshot, stats: TransportStats | null): void {
    this.lastStats = stats ?? this.lastStats;
    void this.panel.webview.postMessage({ t: "metrics", snapshot, stats: this.lastStats });
  }

  /** Batches log entries: one message per 500 ms flush, bounded so a burst
   * drops the batch's tail rather than stacking messages. */
  postLog(entry: DevLogEntry): void {
    if (this.pendingLogs.length < MAX_BATCHED_LOGS) this.pendingLogs.push(entry);
    if (this.logTimer) return;
    this.logTimer = setTimeout(() => {
      this.logTimer = null;
      if (this.pendingLogs.length === 0) return;
      void this.panel.webview.postMessage({ t: "logs", entries: this.pendingLogs.splice(0) });
    }, LOG_FLUSH_MS);
  }

  postBackpressure(depth: QueueDepth): void {
    void this.panel.webview.postMessage({
      t: "logs",
      entries: [
        {
          level: "Warning",
          category: "chorus.devcore",
          timestampMs: Date.now(),
          body: `backpressure: ${depth.dropped} frames dropped (queue ${depth.size}/${depth.capacity})`,
        },
      ],
    });
  }

  /** Starts (or restarts) the /sessions poll against the engine HTTP face. */
  pollSessions(engineUrl: string, fetchLike: typeof fetch = fetch): void {
    this.stopTimers();
    const poll = async (): Promise<void> => {
      try {
        const response = await fetchLike(`${engineUrl.replace(/\/$/, "")}/sessions`, {
          signal: AbortSignal.timeout(2000),
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const rows = (await response.json()) as SessionRow[];
        void this.panel.webview.postMessage({ t: "sessions", rows });
      } catch (error) {
        void this.panel.webview.postMessage({
          t: "sessions",
          rows: [],
          error: `${engineUrl}/sessions unreachable (${(error as Error).message})`,
        });
      }
    };
    void poll();
    this.sessionsTimer = setInterval(() => void poll(), SESSIONS_POLL_MS);
  }

  private stopTimers(): void {
    if (this.sessionsTimer) {
      clearInterval(this.sessionsTimer);
      this.sessionsTimer = null;
    }
    if (this.logTimer) {
      clearTimeout(this.logTimer);
      this.logTimer = null;
    }
  }
}
