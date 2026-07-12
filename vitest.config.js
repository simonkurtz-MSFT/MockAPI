import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["tests/frontend/**/*.test.js"],
    coverage: {
      provider: "v8",
      include: ["src/MockAPI/wwwroot/dashboard-core.js"],
      reporter: ["text", "json-summary", "lcov", "cobertura"],
      reportsDirectory: "artifacts/coverage/frontend",
      thresholds: {
        lines: 100,
        branches: 100,
        functions: 100,
        statements: 100,
      },
    },
  },
});
