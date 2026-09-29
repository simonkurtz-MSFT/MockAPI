import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const repositoryRoot = path.resolve(import.meta.dirname, "..", "..");
const { installGitHooks } = require("../../scripts/install-git-hooks.cjs");
const { removeRedundantTarballUrls } = require("../../scripts/lib/pnpm-lock-registry.cjs");

describe("dependency maintenance policy", () => {
  it("uses the same eight-day cooldown across pnpm configuration", () => {
    const npmrc = fs.readFileSync(path.join(repositoryRoot, ".npmrc"), "utf8");
    const workspace = fs.readFileSync(path.join(repositoryRoot, "pnpm-workspace.yaml"), "utf8");
    expect(npmrc).toMatch(/^minimum-release-age=11520$/m);
    expect(workspace).toMatch(/^minimumReleaseAge: 11520$/m);
    expect(workspace).toMatch(/^minimumReleaseAgeIgnoreMissingTime: false$/m);
    expect(workspace).toMatch(/^lockfileIncludeTarballUrl: false$/m);
  });

  it("keeps pnpm pins synchronized and installs lockfile safeguards", () => {
    const packageMetadata = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8"));
    const hook = fs.readFileSync(path.join(repositoryRoot, ".githooks", "pre-commit"), "utf8");
    const lockfile = fs.readFileSync(path.join(repositoryRoot, "pnpm-lock.yaml"), "utf8");

    expect(packageMetadata.packageManager).toBe(`pnpm@${packageMetadata.engines.pnpm}`);
    expect(packageMetadata.scripts["hooks:install"]).toBe("node ./scripts/install-git-hooks.cjs");
    expect(packageMetadata.scripts.postinstall).toContain("normalizeWorkingLockfile()");
    expect(packageMetadata.scripts.prepare).toBe("pnpm run hooks:install");
    expect(hook).toContain("normalizeStagedLockfile()");
    expect(removeRedundantTarballUrls(lockfile)).toBe(lockfile);
  });

  it("skips developer Git hook installation in CI", () => {
    const gitCalls = [];
    const output = [];

    const installed = installGitHooks({
      environment: { CI: "true" },
      runGit: (...arguments_) => gitCalls.push(arguments_),
      writeOutput: (message) => output.push(message),
    });

    expect(installed).toBe(false);
    expect(gitCalls).toEqual([]);
    expect(output).toEqual(["Skipping repository Git hook installation in CI."]);
  });

  it("configures repository Git hooks outside CI", () => {
    const gitCalls = [];

    const installed = installGitHooks({
      environment: {},
      runGit: (...arguments_) => gitCalls.push(arguments_),
      writeOutput: () => {},
    });

    expect(installed).toBe(true);
    expect(gitCalls).toEqual([["git", ["config", "core.hooksPath", ".githooks"], { stdio: "inherit" }]]);
  });

  it("exposes cooldown-aware dependency and pnpm updates in both developer CLIs", () => {
    const powershellCli = fs.readFileSync(path.join(repositoryRoot, "start.ps1"), "utf8");
    const bashCli = fs.readFileSync(path.join(repositoryRoot, "start.sh"), "utf8");

    for (const cli of [powershellCli, bashCli]) {
      expect(cli).toContain("dependencies-update");
      expect(cli).toContain("pnpm-update");
      expect(cli).toContain("dependency-updater.cjs");
      expect(cli).toContain("pnpm-updater.cjs");
      expect(cli).toContain("core.hooksPath");
    }
  });
});
