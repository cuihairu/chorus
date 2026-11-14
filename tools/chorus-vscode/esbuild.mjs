// Bundles the extension (and the smoke suite VS Code loads separately) from
// TypeScript source; the devcore core rides in from source via tsconfig
// paths, so the .vsix carries one self-contained bundle and no runtime
// dependencies.
import * as esbuild from "esbuild";

const common = {
  bundle: true,
  format: "cjs",
  platform: "node",
  target: "node18",
  external: ["vscode"],
  sourcemap: "inline",
  logLevel: "info",
};

await esbuild.build({
  ...common,
  entryPoints: ["src/extension.ts"],
  outfile: "dist/extension.js",
});

await esbuild.build({
  ...common,
  entryPoints: ["src/smokeSuite.ts"],
  outfile: "dist-smoke/smokeSuite.js",
});
