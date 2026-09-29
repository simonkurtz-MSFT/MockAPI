import { createRequire } from "node:module";
import path from "node:path";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const {
  getConfiguredRegistry,
  getEligiblePnpmRelease,
  getMinimumReleaseAge,
  setPnpmPins,
  updatePnpm,
} = require("../../scripts/lib/pnpm-updater.cjs");

describe("pnpm updater", () => {
  it("reads a positive cooldown and rejects malformed values", () => {
    expect(getMinimumReleaseAge("minimum-release-age=11520\n")).toBe(11520);
    for (const npmrc of ["", "minimum-release-age=0", "minimum-release-age=-1", "minimum-release-age=8days"]) {
      expect(() => getMinimumReleaseAge(npmrc)).toThrow("Expected a positive minimum-release-age in .npmrc.");
    }
  });

  it("selects the newest stable release at or before the exact cutoff", () => {
    const metadata = {
      versions: {
        "12.9.0": {},
        "12.10.0": {},
        "12.11.0": {},
        "13.0.0-beta.1": {},
        "14.0.0": { deprecated: "retired" },
      },
      time: {
        "12.9.0": "2026-01-01T00:00:00Z",
        "12.10.0": "2026-01-02T00:00:00Z",
        "12.11.0": "2026-01-02T00:00:01Z",
      },
    };

    expect(getEligiblePnpmRelease(metadata, 11520, new Date("2026-01-10T00:00:00Z")).version).toBe("12.10.0");
  });

  it("validates registry URLs and uses the public registry when pnpm has no override", () => {
    expect(getConfiguredRegistry(() => "undefined\n").href).toBe("https://registry.npmjs.org/");
    expect(() => getConfiguredRegistry(() => "file:///private/feed")).toThrow(
      "The configured npm registry must be an absolute HTTP(S) URL without a query or fragment."
    );
  });

  it("updates both manifest pins and passes the cooldown to pnpm self-update", () => {
    const repositoryRoot = path.join("C:", "fixture");
    const npmrcPath = path.join(repositoryRoot, ".npmrc");
    const manifestPath = path.join(repositoryRoot, "package.json");
    const originalManifest = JSON.stringify(
      {
        name: "fixture",
        packageManager: "pnpm@12.0.0",
        engines: { node: ">=26", pnpm: "12.0.0" },
      },
      null,
      2
    );
    const files = new Map([
      [npmrcPath, "minimum-release-age=11520\n"],
      [manifestPath, `${originalManifest}\n`],
    ]);
    const calls = [];
    const previousCi = process.env.CI;
    process.env.CI = "original";
    try {
      const release = updatePnpm({
        repositoryRoot,
        metadata: {
          versions: { "12.4.2": {} },
          time: { "12.4.2": "2026-01-01T00:00:00Z" },
        },
        now: new Date("2026-01-10T00:00:00Z"),
        readFileSync: (file) => files.get(file),
        writeFileSync: (file, content) => files.set(file, content),
        runCommand: (command, arguments_, cwd) => {
          calls.push({ command, arguments_, cwd, ci: process.env.CI });
          return arguments_[0] === "--version" ? "12.4.2\n" : "";
        },
      });

      expect(release.version).toBe("12.4.2");
      expect(calls[0]).toEqual({
        command: "pnpm",
        arguments_: ["self-update", "12.4.2", "--config.minimum-release-age=11520"],
        cwd: repositoryRoot,
        ci: "true",
      });
      expect(JSON.parse(files.get(manifestPath))).toMatchObject({
        packageManager: "pnpm@12.4.2",
        engines: { pnpm: "12.4.2" },
      });
      expect(process.env.CI).toBe("original");
    } finally {
      if (previousCi === undefined) delete process.env.CI;
      else process.env.CI = previousCi;
    }
  });

  it("restores the manifest when the active pnpm version does not match", () => {
    const repositoryRoot = path.join("C:", "fixture");
    const npmrcPath = path.join(repositoryRoot, ".npmrc");
    const manifestPath = path.join(repositoryRoot, "package.json");
    const originalManifest = '{"packageManager":"pnpm@12.0.0","engines":{"node":">=26","pnpm":"12.0.0"}}\n';
    const files = new Map([
      [npmrcPath, "minimum-release-age=11520\n"],
      [manifestPath, originalManifest],
    ]);

    expect(() =>
      updatePnpm({
        repositoryRoot,
        metadata: {
          versions: { "12.4.2": {} },
          time: { "12.4.2": "2026-01-01T00:00:00Z" },
        },
        now: new Date("2026-01-10T00:00:00Z"),
        readFileSync: (file) => files.get(file),
        writeFileSync: (file, content) => files.set(file, content),
        runCommand: (command, arguments_) => (arguments_[0] === "--version" ? "12.0.0\n" : ""),
      })
    ).toThrow("Expected pnpm 12.4.2");
    expect(files.get(manifestPath)).toBe(originalManifest);
  });

  it("updates only existing exact pnpm pins", () => {
    expect(
      JSON.parse(setPnpmPins('{"packageManager":"pnpm@12.0.0","engines":{"node":">=26","pnpm":"12.0.0"}}', "12.4.2"))
    ).toMatchObject({ packageManager: "pnpm@12.4.2", engines: { pnpm: "12.4.2" } });
    expect(() => setPnpmPins('{"packageManager":"pnpm@12.0.0"}', "12.4.2")).toThrow(
      "Expected pnpm packageManager and engines pins"
    );
  });
});
