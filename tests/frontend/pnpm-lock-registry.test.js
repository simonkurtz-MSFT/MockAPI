import { createRequire } from "node:module";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const {
  normalizeStagedLockfile,
  normalizeWorkingLockfile,
  removeRedundantTarballUrls,
} = require("../../scripts/lib/pnpm-lock-registry.cjs");

describe("pnpm lockfile registry normalization", () => {
  it("removes registry tarball URLs only when an integrity hash remains", () => {
    const lockfile = [
      "resolution: {integrity: sha512-first, tarball: https://registry.npmjs.org/pkg/-/pkg-1.0.0.tgz}",
      "resolution: {tarball: https://feed.example/npm/pkg/-/pkg-1.0.0.tgz, integrity: sha512-second}",
      "resolution: {tarball: https://downloads.example/pkg-1.0.0.tgz}",
      "homepage: https://example.com/not-a-resolution",
    ].join("\n");

    expect(removeRedundantTarballUrls(lockfile)).toBe(
      [
        "resolution: {integrity: sha512-first}",
        "resolution: {integrity: sha512-second}",
        "resolution: {tarball: https://downloads.example/pkg-1.0.0.tgz}",
        "homepage: https://example.com/not-a-resolution",
      ].join("\n")
    );
  });

  it("normalizes the working lockfile only when needed", () => {
    const writeFileSync = vi.fn();
    expect(
      normalizeWorkingLockfile({
        readFileSync: () => "resolution: {integrity: sha512-test, tarball: https://feed.example/pkg-1.0.0.tgz}\n",
        writeFileSync,
      })
    ).toBe(true);
    expect(writeFileSync).toHaveBeenCalledWith("pnpm-lock.yaml", "resolution: {integrity: sha512-test}\n");

    writeFileSync.mockClear();
    expect(
      normalizeWorkingLockfile({
        readFileSync: () => "resolution: {integrity: sha512-test}\n",
        writeFileSync,
      })
    ).toBe(false);
    expect(writeFileSync).not.toHaveBeenCalled();
  });

  it("updates the staged blob without staging unrelated working-lockfile edits", () => {
    const stagedLockfile = "resolution: {integrity: sha512-test, tarball: https://feed.example/pkg-1.0.0.tgz}\n";
    const calls = [];
    const writeFileSync = vi.fn();
    const execFileSync = (command, arguments_, options = {}) => {
      calls.push({ command, arguments_, options });
      if (arguments_[0] === "show") return stagedLockfile;
      if (arguments_[0] === "hash-object") return "object-id\n";
      return "";
    };

    expect(
      normalizeStagedLockfile({
        execFileSync,
        readFileSync: () => `${stagedLockfile}unstaged: true\n`,
        writeFileSync,
      })
    ).toBe(true);
    expect(calls[1].options.input).toBe("resolution: {integrity: sha512-test}\n");
    expect(calls[2].arguments_).toEqual(["update-index", "--cacheinfo", "100644,object-id,pnpm-lock.yaml"]);
    expect(writeFileSync).toHaveBeenCalledWith(
      "pnpm-lock.yaml",
      "resolution: {integrity: sha512-test}\nunstaged: true\n"
    );
  });
});
