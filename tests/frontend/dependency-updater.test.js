import { createRequire } from "node:module";
import path from "node:path";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const {
  applyDependencyUpdates,
  COUPLED_DEPENDENCIES,
  getEligibleStableRelease,
  selectDependencyUpdates,
} = require("../../scripts/lib/dependency-updater.cjs");

describe("dependency updater", () => {
  it("selects the newest stable non-deprecated release at the cooldown cutoff", () => {
    const release = getEligibleStableRelease(
      "example",
      {
        versions: {
          "2.9.0": {},
          "2.10.0": {},
          "2.11.0": {},
          "3.0.0-alpha.1": {},
          "4.0.0": { deprecated: "retired" },
        },
        time: {
          "2.9.0": "2026-01-01T00:00:00Z",
          "2.10.0": "2026-01-02T00:00:00Z",
          "2.11.0": "2026-01-02T00:00:01Z",
        },
      },
      11520,
      new Date("2026-01-10T00:00:00Z")
    );

    expect(release.version).toBe("2.10.0");
  });

  it("replaces prerelease pins but refuses to downgrade stable pins", () => {
    const metadata = {
      versions: { "3.9.4": {}, "4.0.0-alpha.1": {} },
      time: { "3.9.4": "2026-01-01T00:00:00Z" },
    };
    expect(
      selectDependencyUpdates(
        { devDependencies: { prettier: "4.0.0-alpha.1" } },
        new Map([["prettier", metadata]]),
        11520,
        new Date("2026-01-10T00:00:00Z")
      ).devDependencies[0].version
    ).toBe("3.9.4");
    expect(() =>
      selectDependencyUpdates(
        { devDependencies: { prettier: "4.0.0" } },
        new Map([["prettier", metadata]]),
        11520,
        new Date("2026-01-10T00:00:00Z")
      )
    ).toThrow("refusing an automatic downgrade");
  });

  it("leaves dependencies coupled to external runtime assets for coordinated updates", () => {
    const updateGroups = selectDependencyUpdates(
      { devDependencies: { "@playwright/test": "1.61.1" } },
      new Map(),
      11520,
      new Date("2026-01-10T00:00:00Z")
    );

    expect(COUPLED_DEPENDENCIES.has("@playwright/test")).toBe(true);
    expect(updateGroups.devDependencies).toEqual([]);
  });

  it("updates exact groups with the cooldown and refreshes compatible transitives", () => {
    const repositoryRoot = path.join("C:", "fixture");
    const calls = [];
    let normalized = false;
    applyDependencyUpdates({
      repositoryRoot,
      updateGroups: {
        dependencies: [{ name: "runtime", version: "2.0.0" }],
        devDependencies: [{ name: "tool", version: "3.0.0" }],
      },
      minimumReleaseAge: 11520,
      readFileSync: (file) => (file.endsWith("package.json") ? "{}\n" : "lockfileVersion: '9.0'\n"),
      writeFileSync: () => {
        throw new Error("Rollback should not run.");
      },
      normalizeLockfile: () => {
        normalized = true;
      },
      runCommand: (command, arguments_, cwd) => calls.push({ command, arguments_, cwd }),
    });

    expect(calls).toEqual([
      {
        command: "pnpm",
        arguments_: ["add", "--save-exact", "runtime@2.0.0", "--config.minimum-release-age=11520"],
        cwd: repositoryRoot,
      },
      {
        command: "pnpm",
        arguments_: ["add", "--save-dev", "--save-exact", "tool@3.0.0", "--config.minimum-release-age=11520"],
        cwd: repositoryRoot,
      },
      {
        command: "pnpm",
        arguments_: ["update", "--config.minimum-release-age=11520"],
        cwd: repositoryRoot,
      },
    ]);
    expect(normalized).toBe(true);
  });

  it("restores both manifests when pnpm fails", () => {
    const repositoryRoot = path.join("C:", "fixture");
    const writes = [];
    expect(() =>
      applyDependencyUpdates({
        repositoryRoot,
        updateGroups: { dependencies: [], devDependencies: [] },
        minimumReleaseAge: 11520,
        readFileSync: (file) => (file.endsWith("package.json") ? "manifest\n" : "lockfile\n"),
        writeFileSync: (file, content) => writes.push({ file, content }),
        normalizeLockfile: () => false,
        runCommand: () => {
          throw new Error("pnpm failed");
        },
      })
    ).toThrow("pnpm failed");
    expect(writes).toEqual([
      { file: path.join(repositoryRoot, "package.json"), content: "manifest\n" },
      { file: path.join(repositoryRoot, "pnpm-lock.yaml"), content: "lockfile\n" },
    ]);
  });
});
