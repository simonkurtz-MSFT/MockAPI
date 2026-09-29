import { mkdir, rm } from "node:fs/promises";
import path from "node:path";

/**
 * Requires serial access to the shared backend and removes only the previous test configuration.
 * @param {import("@playwright/test").FullConfig} config Resolved Playwright configuration.
 * @returns {Promise<void>} Resolves after preparing test persistence.
 * @throws {Error} More than one worker would race configuration and statistics across feature suites.
 */
export default async function globalSetup(config) {
  if (config.workers !== 1) {
    throw new Error(
      "Dashboard browser suites share one backend. Run with --workers=1 to isolate configuration and statistics."
    );
  }
  const directory = path.resolve("artifacts/playwright");
  await mkdir(directory, { recursive: true });
  await rm(path.join(directory, "mockapi.json"), { force: true });
}
