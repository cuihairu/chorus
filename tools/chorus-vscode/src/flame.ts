// The flame graph entry (IDE-EXT): a 30 s dotnet-trace sample of the engine
// process, converted to speedscope on the way out, then the operator opens
// the file at speedscope.app (the same flow BOTS-3 documents on the control
// face). The command builds its argv here so the pure logic is testable
// without the vscode module.

import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

export interface SampleCommand {
  file: string;
  args: string[];
  /** Where the .speedscope.json lands. */
  output: string;
}

/** Builds the dotnet-trace invocation: collect + convert in one step
 * (--format speedscope). dotnet-trace writes the raw trace to --output
 * verbatim and the converter rewrites it beside itself via ChangeExtension —
 * so naming the collect output "*.speedscope.json" would yield
 * "*.speedscope.speedscope.json". Collect to .nettrace; the file the
 * operator opens is the extension-swapped sibling. Everything lives under
 * the OS temp dir so a crashed run never leaves junk inside a workspace. */
export function buildSampleCommand(pid: number, seconds: number, outDir: string): SampleCommand {
  const duration = `${Math.max(1, Math.floor(seconds))}`;
  const trace = path.join(outDir, `chorus-engine-${pid}-${Date.now()}.nettrace`);
  const output = trace.replace(/\.nettrace$/, ".speedscope.json");
  return {
    file: "dotnet-trace",
    args: [
      "collect",
      "--pid",
      String(pid),
      "--duration",
      `00:00:${duration.padStart(2, "0")}`,
      "--format",
      "speedscope",
      "--output",
      trace,
    ],
    output,
  };
}

export class FlameToolMissingError extends Error {
  constructor() {
    super("dotnet-trace is not on PATH — install it with: dotnet tool install -g dotnet-trace");
    this.name = "FlameToolMissingError";
  }
}

/** The temp directory the samples land in, made on demand. */
export function sampleDirectory(): string {
  const dir = path.join(os.tmpdir(), "chorus-flame");
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

/** Verifies the tool answers --version; one failure line otherwise. */
export function requireTool(file: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const child = spawn(file, ["--version"], { stdio: "ignore" });
    child.on("error", () => reject(new FlameToolMissingError()));
    child.on("exit", (code) => {
      if (code === 0) resolve();
      else reject(new FlameToolMissingError());
    });
  });
}

/** Runs the sample to completion; resolves with the speedscope file path. */
export function runSample(command: SampleCommand, onSpawned?: () => void): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(command.file, command.args, { stdio: "ignore" });
    onSpawned?.();
    child.on("error", reject);
    child.on("exit", (code) => {
      if (code === 0 && fs.existsSync(command.output)) resolve(command.output);
      else reject(new Error(`dotnet-trace exited with ${code ?? "a signal"}`));
    });
  });
}
