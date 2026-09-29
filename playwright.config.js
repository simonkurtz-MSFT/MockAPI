import path from "node:path";
import { defineConfig, devices } from "@playwright/test";

const port = 8091;
const baseURL = `http://127.0.0.1:${port}`;
const localChromium = process.env.CI ? {} : { channel: "msedge" };

export default defineConfig({
  testDir: "tests/browser",
  globalSetup: "./tests/browser/global-setup.js",
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [
    ["list"],
    ["./scripts/PlaywrightNoSkipsReporter.mjs"],
    ["html", { outputFolder: "artifacts/playwright/report", open: "never" }],
    ["junit", { outputFile: "artifacts/playwright/results.xml" }],
  ],
  outputDir: "artifacts/playwright/results",
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  webServer: {
    command:
      "dotnet run --project src/MockAPI/MockAPI.csproj --configuration Release --no-self-contained -p:UseAppHost=false",
    url: `${baseURL}/health/ready`,
    reuseExistingServer: false,
    timeout: 60_000,
    env: {
      ...process.env,
      ASPNETCORE_URLS: baseURL,
      MockApi__AllowEmptyConfiguration: "true",
      MockApi__ConfigurationPath: path.resolve("artifacts/playwright/mockapi.json"),
      MockApi__ManagementPermitLimit: "10000",
    },
  },
  projects: [
    { name: "chromium-desktop", use: { ...devices["Desktop Chrome"], ...localChromium } },
    { name: "chromium-mobile", use: { ...devices["Pixel 7"], ...localChromium } },
    {
      name: "chromium-accessibility-preferences",
      use: {
        ...devices["Desktop Chrome"],
        ...localChromium,
        colorScheme: "dark",
        forcedColors: "active",
        reducedMotion: "reduce",
      },
    },
  ],
});
