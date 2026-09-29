const { defineConfig, devices } = require("@playwright/test");

module.exports = defineConfig({
  testDir: "./tests/site",
  outputDir: "./artifacts/playwright-site",
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  use: { baseURL: "http://127.0.0.1:4173/MockAPI/", trace: "retain-on-failure" },
  webServer: {
    command: "node scripts/serve-site.cjs",
    url: "http://127.0.0.1:4173/MockAPI/",
    reuseExistingServer: false,
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "mobile", use: { ...devices["iPhone 13"], defaultBrowserType: "chromium" } },
    { name: "dark", use: { ...devices["Desktop Chrome"], colorScheme: "dark" } },
  ],
});
