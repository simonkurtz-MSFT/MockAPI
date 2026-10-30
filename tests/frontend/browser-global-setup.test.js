import path from "node:path";
import { readFileSync } from "node:fs";
import { mkdir, rm } from "node:fs/promises";
import { beforeEach, describe, expect, it, vi } from "vitest";
import browserConfig from "../../playwright.config.js";
import globalSetup from "../browser/global-setup.js";

vi.mock("node:fs/promises", () => ({ mkdir: vi.fn(), rm: vi.fn() }));

describe("browser backend isolation", () => {
  beforeEach(() => vi.clearAllMocks());

  it("keeps ordinary dashboard network interception deterministic and backend access serial", () => {
    expect(browserConfig.use.serviceWorkers).toBe("block");
    expect(browserConfig.fullyParallel).toBe(false);
    expect(browserConfig.workers).toBe(1);
  });

  it("runs every Chromium profile once on isolated CI runners without discarding failed-profile evidence", () => {
    const workflow = readFileSync(new URL("../../.github/workflows/quality.yml", import.meta.url), "utf8");
    const browserJob = workflow.slice(workflow.indexOf("  browser-chromium:"));
    const projects = [...browserJob.matchAll(/^          - (chromium-[a-z-]+)$/gm)].map(([, project]) => project);
    expect(projects).toEqual(browserConfig.projects.map(({ name }) => name));
    expect(browserJob).toContain("fail-fast: false");
    expect(browserJob).toContain('run: pnpm run test:browser --project "${{ matrix.project }}"');
    expect(browserJob).toContain("name: playwright-${{ matrix.project }}-results");
    expect(browserJob).toContain("if: always()");
    expect(browserJob).not.toContain("--workers");
  });

  it("rejects concurrent workers before changing shared persistence", async () => {
    await expect(globalSetup({ workers: 2 })).rejects.toThrow("--workers=1");
    expect(mkdir).not.toHaveBeenCalled();
    expect(rm).not.toHaveBeenCalled();
  });

  it("prepares serial runs by removing only the known test configuration", async () => {
    await globalSetup({ workers: 1 });
    const directory = path.resolve("artifacts/playwright");
    expect(mkdir).toHaveBeenCalledExactlyOnceWith(directory, { recursive: true });
    expect(rm).toHaveBeenCalledExactlyOnceWith(path.join(directory, "mockapi.json"), { force: true });
  });
});
