import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["tests/frontend/**/*.test.js"],
    reporters: ["default", "junit"],
    outputFile: { junit: "artifacts/coverage/frontend/frontend-tests.xml" },
    coverage: {
      provider: "v8",
      include: [
        "src/MockAPI/wwwroot/dashboard-core.js",
        "src/MockAPI/wwwroot/dashboard-dom.js",
        "src/MockAPI/wwwroot/dashboard-endpoint-editor.js",
        "src/MockAPI/wwwroot/dashboard-management.js",
        "src/MockAPI/wwwroot/dashboard-layout.js",
        "src/MockAPI/wwwroot/dashboard-preferences.js",
        "src/MockAPI/wwwroot/dashboard-sync.js",
        "src/MockAPI/wwwroot/dashboard-test-request.js",
        "src/MockAPI/wwwroot/dashboard-tutorial.js",
      ],
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
