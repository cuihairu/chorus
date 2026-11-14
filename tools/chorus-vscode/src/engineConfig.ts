// Settings → endpoints. The defaults mirror the engine's own (Ipc section +
// the dev launch profile), so a stock `--devtools` engine attach with zero
// configuration.
import * as vscode from "vscode";
import { AttachEndpoint, AttachSession } from "@chorus/devcore";

function configuration(): vscode.WorkspaceConfiguration {
  return vscode.workspace.getConfiguration("chorus");
}

/** The devtools endpoint the engine binds for this platform. */
export function resolveEndpoint(): AttachEndpoint {
  const settings = configuration();
  return AttachSession.endpoint(process.platform, settings.get("unixSocketDir"), settings.get("pipePrefix"));
}

/** The engine host's HTTP face (the /sessions snapshot rides it). */
export function resolveEngineUrl(): string {
  return configuration().get("engineUrl") ?? "http://127.0.0.1:5067";
}
