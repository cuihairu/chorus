// The smoke suite VS Code itself executes (smoke/run.mjs hands this file to
// @vscode/test-electron): activation, command registration, and a detach
// against no engine — the smallest honest proof the extension host pulls the
// extension up without blowing up.
import * as assert from "node:assert";
import * as vscode from "vscode";

export async function run(): Promise<void> {
  const extension = vscode.extensions.getExtension("cuihairu.chorus-vscode");
  assert.ok(extension, "the Chorus extension is present in the development host");
  await extension!.activate();

  const commands = await vscode.commands.getCommands(true);
  for (const wanted of ["chorus.attach", "chorus.detach", "chorus.openPanel", "chorus.sampleFlameGraph"]) {
    assert.ok(commands.includes(wanted), `command ${wanted} is registered`);
  }

  // Detach with no engine around: a clean no-op, not a throw.
  await vscode.commands.executeCommand("chorus.detach");

  // Sample with no attach: a friendly error, not a crash. The command shows
  // a message and returns; nothing observable to assert beyond "it returned".
  await vscode.commands.executeCommand("chorus.sampleFlameGraph");
}
