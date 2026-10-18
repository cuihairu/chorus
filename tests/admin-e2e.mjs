import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createServer } from 'node:net';
import { pbkdf2Sync } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { chromium } from 'playwright';

const root = path.resolve(import.meta.dirname, '..');
const appAssembly = path.join(root, 'src/Chorus.ControlHost/bin/Release/net10.0/Chorus.ControlHost.dll');
const controlKey = 'admin-e2e-control-key-long-enough';
const password = 'admin-e2e-password';
const roomCount = 100;
const connectionsPerRoom = 5;
const historyCount = 720;

function passwordHash(value) {
  const salt = Buffer.from('chorus-admin-e2e-salt');
  const subkey = pbkdf2Sync(value, salt, 100_000, 32, 'sha512');
  const header = Buffer.alloc(13);
  header[0] = 1;
  header.writeUInt32BE(2, 1); // Identity V3: HMACSHA512
  header.writeUInt32BE(100_000, 5);
  header.writeUInt32BE(salt.length, 9);
  return Buffer.concat([header, salt, subkey]).toString('base64');
}

async function freePort() {
  const server = createServer();
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  const { port } = server.address();
  await new Promise(resolve => server.close(resolve));
  return port;
}

async function waitForHealth(baseUrl, process, getLogs) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (process.exitCode !== null) throw new Error(`ControlHost exited early.\n${getLogs()}`);
    try {
      const response = await fetch(`${baseUrl}/health`);
      if (response.ok) return;
    } catch { /* wait for Kestrel startup */ }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error(`ControlHost did not become healthy.\n${getLogs()}`);
}

async function internalPost(baseUrl, route, body) {
  const response = await fetch(`${baseUrl}/internal/${route}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'X-Control-Key': controlKey },
    body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(`Seed ${route} failed with ${response.status}: ${await response.text()}`);
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

function percentile(samples, fraction) {
  const sorted = [...samples].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * fraction) - 1)];
}

const port = await freePort();
const baseUrl = `http://127.0.0.1:${port}`;
const dataDir = mkdtempSync(path.join(tmpdir(), 'chorus-admin-e2e-'));
const hash = passwordHash(password);
let logs = '';
let app;
let browser;
try {
  assert.ok((await import('node:fs')).existsSync(appAssembly), `Build ControlHost first: ${appAssembly}`);
  app = spawn('dotnet', [appAssembly], {
    cwd: path.join(root, 'src/Chorus.ControlHost'),
    env: {
      ...process.env,
      ASPNETCORE_URLS: baseUrl,
      DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE: 'false',
      Control__Key: controlKey,
      Control__Database: path.join(dataDir, 'control.db'),
      Admin__PublicOrigin: baseUrl,
      Admin__Users__0__Name: 'admin',
      Admin__Users__0__Role: 'admin',
      Admin__Users__0__PasswordHash: hash,
      Admin__Users__1__Name: 'viewer',
      Admin__Users__1__Role: 'viewer',
      Admin__Users__1__PasswordHash: hash
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  const capture = chunk => { logs = (logs + chunk.toString()).slice(-12_000); };
  app.stdout.on('data', capture);
  app.stderr.on('data', capture);
  await waitForHealth(baseUrl, app, () => logs);

  await internalPost(baseUrl, 'nodes/report', {
    id: 'worker-a', instanceId: 'worker-boot', machineId: 'machine-a', role: 'worker',
    endpoint: { host: '127.0.0.1', port: 3101 }, capacity: roomCount + 10
  });
  await internalPost(baseUrl, 'nodes/report', {
    id: 'gateway-a', instanceId: 'gateway-boot', machineId: 'machine-a', role: 'gateway',
    endpoint: { host: '127.0.0.1', port: 4101 }, capacity: 0
  });
  for (let i = 0; i < roomCount; i++) {
    const room = await internalPost(baseUrl, 'rooms/assign', {
      id: `admin-e2e-room-${String(i).padStart(3, '0')}`,
      platform: ['douyin', 'bilibili', 'kuaishou', 'test'][i % 4], mode: 'default', region: 'default'
    });
    for (let client = 0; client < connectionsPerRoom; client++) {
      await internalPost(baseUrl, 'connections/connect', {
        sessionId: room.id, clientId: `client-${client}`, userId: `user-${i}-${client}`,
        role: 'viewer', gatewayId: 'gateway-a', gatewayInstance: 'gateway-boot',
        generation: `generation-${i}-${client}`
      });
    }
  }
  // Seed the bounded one-hour history window for a repeatable query-volume gate.
  const db = new DatabaseSync(path.join(dataDir, 'control.db'));
  const now = Date.now();
  const history = Array.from({ length: historyCount }, (_, i) => ({
    atMs: now - (historyCount - i - 1) * 5_000,
    machines: 1, workers: 1, gateways: 1, rooms: roomCount, activeRooms: 0,
    connections: roomCount * connectionsPerRoom, onlineUsers: roomCount * connectionsPerRoom,
    onlineStreamers: 0, queued: 0, runningMatches: 0,
    roomsByPlatform: { douyin: 25, bilibili: 25, kuaishou: 25, test: 25 },
    onlineUsersByPlatform: { douyin: 125, bilibili: 125, kuaishou: 125, test: 125 }
  }));
  db.prepare('INSERT INTO records(kind,id,body) VALUES(?,?,?) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body')
    .run('system', 'history', JSON.stringify(history));
  db.close();
  await internalPost(baseUrl, 'nodes/report', {
    id: 'worker-a', instanceId: 'worker-boot', machineId: 'machine-a', role: 'worker',
    endpoint: { host: '127.0.0.1', port: 3101 }, capacity: roomCount + 10
  });
  await internalPost(baseUrl, 'nodes/report', {
    id: 'gateway-a', instanceId: 'gateway-boot', machineId: 'machine-a', role: 'gateway',
    endpoint: { host: '127.0.0.1', port: 4101 }, capacity: 0
  });

  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  page.on('pageerror', error => console.error(`Browser error: ${error.message}`));
  page.on('console', message => { if (message.type() === 'error') console.error(`Browser console: ${message.text()}`); });
  const homeResponse = await page.goto(baseUrl);
  console.log(`Admin home returned ${homeResponse?.status()}`);
  await page.waitForFunction(() => !document.querySelector('#login')?.hidden || !document.querySelector('#app')?.hidden);
  await page.locator('#login').waitFor({ state: 'visible' });
  await page.locator('input[name="name"]').fill('admin');
  await page.locator('input[name="password"]').fill('wrong-password');
  await page.getByRole('button', { name: '登录' }).click();
  await page.locator('#login-error').waitFor({ state: 'visible' });
  await page.locator('input[name="password"]').fill(password);
  await page.getByRole('button', { name: '登录' }).click();
  await page.locator('#app').waitFor({ state: 'visible' });
  await page.getByText('100', { exact: true }).first().waitFor();

  const queryResults = await page.evaluate(async paths => {
    const output = {};
    for (const endpoint of paths) {
      const times = [];
      for (let i = 0; i < 30; i++) {
        const started = performance.now();
        const response = await fetch(endpoint, { cache: 'no-store' });
        if (!response.ok) throw new Error(`${endpoint} returned ${response.status}`);
        await response.json();
        times.push(performance.now() - started);
      }
      output[endpoint] = times;
    }
    return output;
  }, ['/api/summary', '/api/rooms', '/api/connections', '/api/history']);
  for (const [endpoint, times] of Object.entries(queryResults)) {
    const p95 = percentile(times, 0.95);
    const p99 = percentile(times, 0.99);
    console.log(`SLO ${endpoint}: p95=${p95.toFixed(1)}ms p99=${p99.toFixed(1)}ms n=${times.length}`);
    assert.ok(p95 <= 250, `${endpoint} p95 ${p95.toFixed(1)}ms exceeds 250ms`);
    assert.ok(p99 <= 500, `${endpoint} p99 ${p99.toFixed(1)}ms exceeds 500ms`);
  }
  assert.equal(await page.locator('#history-platform').count(), 1);
  await page.locator('#history-platform').selectOption('douyin');
  await page.locator('#history-platform').waitFor({ state: 'visible' });
  await page.waitForTimeout(250);

  await page.setViewportSize({ width: 375, height: 812 });
  await page.waitForTimeout(100);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
  assert.equal(overflow, false, 'Admin layout must not overflow a 375px viewport');
  await page.setViewportSize({ width: 1280, height: 900 });

  let failOnce = true;
  await page.route('**/api/rooms', route => {
    if (failOnce) { failOnce = false; return route.abort('failed'); }
    return route.continue();
  });
  await page.locator('#refresh').click();
  await page.waitForFunction(() => document.querySelector('#live-status')?.textContent === '数据已过期');
  await page.unroute('**/api/rooms');
  await page.locator('#refresh').click();
  await page.waitForFunction(() => document.querySelector('#live-status')?.textContent === '已同步');

  const drainStatus = await page.evaluate(async () => {
    const response = await fetch('/api/nodes/worker-a/drain', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ draining: true })
    });
    return response.status;
  });
  assert.equal(drainStatus, 200);
  await page.locator('a[href="#audit"]').click();
  await page.getByText('node.drain', { exact: true }).waitFor();

  const viewerPage = await (await browser.newContext()).newPage();
  await viewerPage.goto(baseUrl);
  await viewerPage.locator('input[name="name"]').fill('viewer');
  await viewerPage.locator('input[name="password"]').fill(password);
  await viewerPage.getByRole('button', { name: '登录' }).click();
  await viewerPage.locator('#app').waitFor({ state: 'visible' });
  const forbiddenStatus = await viewerPage.evaluate(async () => {
    const response = await fetch('/api/nodes/worker-a/drain', {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ draining: false })
    });
    return response.status;
  });
  assert.equal(forbiddenStatus, 403);
  console.log(`Admin browser E2E passed: ${roomCount} rooms, ${roomCount * connectionsPerRoom} connections, ${historyCount} history samples.`);
} finally {
  if (browser) await browser.close();
  if (app && app.exitCode === null) {
    app.kill('SIGTERM');
    await Promise.race([new Promise(resolve => app.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 5_000))]);
    if (app.exitCode === null) app.kill('SIGKILL');
  }
  rmSync(dataDir, { recursive: true, force: true });
}
