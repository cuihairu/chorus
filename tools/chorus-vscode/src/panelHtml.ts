// The Chorus panel page: one self-contained HTML document (no external
// resources — every style and script is inline, so the CSP is a nonce on the
// single inline script). The extension pushes state in; the page renders
// CPU / working-set curves, the filtered log stream, and the session
// snapshot table. Built as a pure string so it is unit-testable.

export interface PanelPageArgs {
  nonce: string;
}

/** The one-way message protocol the extension pushes (webview → extension
 * messages may come later; nothing is consumed today). */
export const PANEL_SCRIPT = `
  const vscode = acquireVsCodeApi();
  const state = {
    cpu: [], mem: [], maxPoints: 300,
    logs: [], maxLogs: 2000, logDropped: 0,
    filter: "", level: "All",
    sessions: [], sessionsError: "",
    link: "off", metricsState: "off", logState: "off", pid: null, dropStats: null,
  };
  const LEVELS = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

  function chart(id, series, max, color, unit) {
    const canvas = document.getElementById(id);
    const ctx = canvas.getContext("2d");
    const w = canvas.width = canvas.clientWidth * devicePixelRatio;
    const h = canvas.height = canvas.clientHeight * devicePixelRatio;
    ctx.clearRect(0, 0, w, h);
    ctx.strokeStyle = "#8884";
    ctx.lineWidth = 1;
    for (let i = 1; i < 4; i++) { const y = (h * i) / 4; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke(); }
    if (series.length > 1) {
      ctx.strokeStyle = color;
      ctx.lineWidth = 2 * devicePixelRatio;
      ctx.beginPath();
      for (let i = 0; i < series.length; i++) {
        const x = (i * w) / (state.maxPoints - 1);
        const y = h - Math.min(1, series[i] / max) * (h - 6) - 3;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
    const last = series.length > 0 ? series[series.length - 1] : null;
    document.getElementById(id + "-now").textContent = last === null ? "—" : last.toFixed(2) + unit;
  }

  function renderCharts() {
    chart("cpuChart", state.cpu, 100, "#4fc1ff", "%");
    const memMb = state.mem.map((b) => b / 1048576);
    const peak = Math.max(64, ...memMb) * 1.2;
    chart("memChart", memMb, peak, "#b180ff", " MB");
    document.getElementById("pid").textContent = state.pid === null ? "—" : String(state.pid);
    const ds = state.dropStats;
    document.getElementById("drops").textContent =
      ds === null ? "—" : \`dropped \${ds.dropped}, malformed \${ds.malformedFrames}, skipped-empty \${ds.skippedEmptyFrames}\`;
  }

  function renderLogs() {
    const list = document.getElementById("logs");
    const needle = state.filter.toLowerCase();
    const minLevel = state.level === "All" ? -1 : LEVELS.indexOf(state.level);
    const visible = [];
    for (let i = state.logs.length - 1; i >= 0 && visible.length < 400; i--) {
      const entry = state.logs[i];
      if (minLevel >= 0 && LEVELS.indexOf(entry.level) < minLevel) continue;
      if (needle && !entry.body.toLowerCase().includes(needle) && !entry.category.toLowerCase().includes(needle)) continue;
      visible.push(entry);
    }
    list.textContent = "";
    for (const entry of visible.reverse()) {
      const row = document.createElement("div");
      row.className = "log " + entry.level.toLowerCase();
      const head = document.createElement("span");
      head.className = "cat";
      head.textContent = \`[\${entry.level}] \${entry.category}\`;
      row.appendChild(head);
      row.appendChild(document.createTextNode(entry.body));
      list.appendChild(row);
    }
    const dropped = document.getElementById("logDropped");
    dropped.textContent = state.logDropped > 0 ? \`(\${state.logDropped} entries dropped by the panel: batch cap + page buffer)\` : "";
  }

  function renderSessions() {
    const wrap = document.getElementById("sessions");
    if (state.sessionsError) { wrap.textContent = "sessions: " + state.sessionsError; return; }
    if (state.sessions.length === 0) { wrap.textContent = "sessions: none"; return; }
    const table = document.createElement("table");
    const header = ["session", "tick", "clients", "max lag", "mailbox", "dropped", "last tick ms"];
    const thead = document.createElement("tr");
    for (const name of header) { const th = document.createElement("th"); th.textContent = name; thead.appendChild(th); }
    table.appendChild(thead);
    for (const s of state.sessions.slice(0, 50)) {
      // The minimal-API face serializes with Web defaults: camelCase.
      const clients = s.clients || [];
      const maxLag = clients.reduce((m, c) => Math.max(m, c.lagTicks || 0), 0);
      const cells = [s.sessionId, s.tickId, clients.length, maxLag, s.mailboxDepth, s.totalDroppedEvents, s.lastTickProcessMs];
      const tr = document.createElement("tr");
      for (const cell of cells) { const td = document.createElement("td"); td.textContent = String(cell); tr.appendChild(td); }
      table.appendChild(tr);
    }
    wrap.textContent = "";
    wrap.appendChild(table);
  }

  function renderHeader() {
    document.getElementById("link").textContent = \`link \${state.link} · metrics \${state.metricsState} · log \${state.logState}\`;
  }

  let chartsQueued = false;
  function queueCharts() {
    if (chartsQueued) return;
    chartsQueued = true;
    requestAnimationFrame(() => { chartsQueued = false; renderCharts(); });
  }

  window.addEventListener("message", (event) => {
    const m = event.data;
    if (m.t === "status") { state.link = m.link; state.metricsState = m.metrics; state.logState = m.log; renderHeader(); }
    else if (m.t === "metrics") {
      state.pid = m.snapshot.processId;
      state.dropStats = m.stats;
      state.cpu.push(m.snapshot.cpuPercent ?? 0);
      state.mem.push(m.snapshot.workingSetBytes);
      if (state.cpu.length > state.maxPoints) state.cpu.shift();
      if (state.mem.length > state.maxPoints) state.mem.shift();
      queueCharts();
    } else if (m.t === "logs") {
      // Loss the page never saw: entries the extension's batch cap discarded
      // before this message existed.
      state.logDropped += m.dropped || 0;
      for (const entry of m.entries) {
        state.logs.push(entry);
        if (state.logs.length > state.maxLogs) { state.logs.shift(); state.logDropped++; }
      }
      renderLogs();
    } else if (m.t === "sessions") { state.sessions = m.rows || []; state.sessionsError = m.error || ""; renderSessions(); }
  });
  renderHeader(); renderCharts(); renderSessions();

  // The toolbar drives the state the renderers read; without these the
  // controls are decoration.
  document.getElementById("filter").addEventListener("input", (event) => {
    state.filter = event.target.value;
    renderLogs();
  });
  document.getElementById("level").addEventListener("change", (event) => {
    state.level = event.target.value;
    renderLogs();
  });
`;

export function panelHtml(args: PanelPageArgs): string {
  return `<!DOCTYPE html>
<html>
<head>
<meta charset="UTF-8">
<!-- The nonce attribute alone enforces nothing; the policy does. No
     external resources exist on this page, so everything but inline style
     and the one nonce'd script is denied. -->
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${args.nonce}';">
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); margin: 8px; }
  h3 { margin: 12px 0 4px 0; font-size: 1.05em; }
  #link { color: var(--vscode-descriptionForeground); margin-bottom: 8px; }
  canvas { width: 100%; height: 110px; display: block; background: var(--vscode-editor-background);
           border: 1px solid var(--vscode-widget-border, #3334); border-radius: 4px; }
  .chart-label { display: flex; justify-content: space-between; font-size: 0.9em; margin-top: 2px; }
  #toolbar { display: flex; gap: 8px; margin: 6px 0; }
  #filter { flex: 1; }
  input, select { background: var(--vscode-input-background); color: var(--vscode-input-foreground);
                  border: 1px solid var(--vscode-input-border, #3334); padding: 3px 6px; border-radius: 2px; }
  #logs { height: 220px; overflow-y: auto; background: var(--vscode-editor-background);
          border: 1px solid var(--vscode-widget-border, #3334); border-radius: 4px; padding: 4px;
          font-family: var(--vscode-editor-font-family); font-size: 0.85em; white-space: pre-wrap; word-break: break-all; }
  .log { padding: 1px 0; }
  .log.warning { color: var(--vscode-editorWarning-foreground); }
  .log.error, .log.critical { color: var(--vscode-editorError-foreground); }
  .log.debug, .log.trace { color: var(--vscode-descriptionForeground); }
  .cat { color: var(--vscode-descriptionForeground); margin-right: 6px; }
  table { border-collapse: collapse; font-size: 0.85em; }
  th, td { border: 1px solid var(--vscode-widget-border, #3334); padding: 2px 8px; text-align: left; }
  th { color: var(--vscode-descriptionForeground); }
</style>
</head>
<body>
  <div id="link">link off · metrics off · log off</div>
  <h3>CPU % <span class="chart-label"><span>engine pid <span id="pid">—</span></span><span id="cpuChart-now">—</span></span></h3>
  <canvas id="cpuChart"></canvas>
  <h3>Working set <span class="chart-label"><span></span><span id="memChart-now">—</span></span></h3>
  <canvas id="memChart"></canvas>
  <div class="chart-label"><span></span><span id="drops">—</span></div>
  <h3>Engine log stream <span id="logDropped"></span></h3>
  <div id="toolbar">
    <input id="filter" placeholder="filter body or category…">
    <select id="level">
      <option>All</option>
      ${["Trace", "Debug", "Information", "Warning", "Error", "Critical"].map((l) => `<option>${l}</option>`).join("")}
    </select>
  </div>
  <div id="logs"></div>
  <h3>Sessions (engine HTTP /sessions)</h3>
  <div id="sessions">sessions: none</div>
  <script nonce="${args.nonce}">${PANEL_SCRIPT}</script>
</body>
</html>`;
}
