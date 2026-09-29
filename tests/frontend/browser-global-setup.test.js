import path from "node:path";
import { mkdir, rm } from "node:fs/promises";
import { beforeEach, describe, expect, it, vi } from "vitest";
import globalSetup from "../browser/global-setup.js";

vi.mock("node:fs/promises", () => ({ mkdir: vi.fn(), rm: vi.fn() }));

describe("browser backend isolation", () => {
  beforeEach(() => vi.clearAllMocks());

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
