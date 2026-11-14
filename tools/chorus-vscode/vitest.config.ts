import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
    // Anything that imports the vscode module runs in the smoke suite inside
    // a real extension host instead; vitest carries the pure logic.
    testTimeout: 15000,
  },
});
