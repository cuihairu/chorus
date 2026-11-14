// Downloads (once, into ~/.vscode-test) a headless VS Code, launches it with
// this extension as the only development extension, and runs the compiled
// smoke suite (dist-smoke/smokeSuite.js) inside its extension host.
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { runTests } from "@vscode/test-electron";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

try {
  await runTests({
    extensionDevelopmentPath: root,
    extensionTestsPath: path.join(root, "dist-smoke", "smokeSuite.js"),
    launchArgs: ["--disable-extensions", "--disable-gpu"],
  });
  console.log("smoke: pass");
} catch (error) {
  console.error("smoke: fail", error);
  process.exit(1);
}
