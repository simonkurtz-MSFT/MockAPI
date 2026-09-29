import { createRequire } from "node:module";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const { validateVersion, readVersion, compareVersions, planTag } = require("../../scripts/release-version.cjs");

describe("application release versions", () => {
  it.each(["1.0.0", "1.0.0-beta.3", "1.0.0-rc.1", "0.0.1", "2.10.4-alpha-2", "1.0.0-0"])("accepts %s", (version) => {
    expect(validateVersion(version)).toBe(version);
  });

  it.each([
    "v1.0.0",
    "01.0.0",
    "1.0",
    "1.0.0-beta.01",
    "1.0.0-",
    "1.0.0+a",
    "1.0.0\n",
    "1.0.0;echo unsafe",
    `1.0.0-${"a".repeat(122)}`,
  ])("rejects %s", (version) => {
    expect(() => validateVersion(version)).toThrow("Invalid application version");
  });

  it("reads only the single application version, ignoring commented examples and dependency versions", () => {
    expect(
      readVersion(
        "<Project><!-- <Version>0.1.0</Version> --><PropertyGroup><Version>1.0.0-beta.3</Version></PropertyGroup><PackageReference Version='10.0.11' /></Project>"
      )
    ).toBe("1.0.0-beta.3");
    expect(() => readVersion("<Project />")).toThrow("exactly one");
    expect(() => readVersion("<Version>1.0.0</Version><Version>2.0.0</Version>")).toThrow("exactly one");
    expect(() => readVersion('<Version>1.0.0</Version><Version Condition="test">2.0.0</Version>')).toThrow(
      "exactly one"
    );
    expect(() => readVersion('<PropertyGroup Condition="test"><Version>1.0.0</Version></PropertyGroup>')).toThrow(
      "exactly one"
    );
  });

  it.each([
    ["1.0.0-alpha", "1.0.0-alpha.1"],
    ["1.0.0-alpha.1", "1.0.0-alpha.beta"],
    ["1.0.0-beta.2", "1.0.0-beta.11"],
    ["1.0.0-beta.11", "1.0.0-rc.1"],
    ["1.0.0-rc.1", "1.0.0"],
    ["1.0.0", "1.0.1"],
    ["1.9.0", "1.10.0"],
    ["1.10.0", "2.0.0"],
  ])("orders %s before %s", (first, second) => {
    expect(compareVersions(first, second)).toBe(-1);
    expect(compareVersions(second, first)).toBe(1);
    expect(compareVersions(first, first)).toBe(0);
  });

  it("does not tag ordinary commits under an unchanged version", () => {
    expect(
      planTag({ version: "1.0.0-beta.3", previousVersion: "1.0.0-beta.3", head: "new", existingCommit: "old" })
    ).toBe("unchanged");
  });

  it("creates a new version tag, allows an exact rerun, and refuses reuse", () => {
    const candidate = { version: "1.0.0-rc.1", previousVersion: "1.0.0-beta.3", head: "new" };
    expect(planTag(candidate)).toBe("create");
    expect(planTag({ ...candidate, existingCommit: "new" })).toBe("existing");
    expect(() => planTag({ ...candidate, existingCommit: "old" })).toThrow("Refusing to move");
    expect(() => planTag({ ...candidate, version: "1.0.0-beta.2" })).toThrow("must be newer");
  });

  it("tags and pushes the validated commit without moving it on later unchanged commits", () => {
    const fixture = fs.mkdtempSync(path.join(os.tmpdir(), "mockapi-version-test-"));
    const remote = path.join(fixture, "remote.git");
    const checkout = path.join(fixture, "checkout");
    const script = path.resolve(import.meta.dirname, "../../scripts/release-version.cjs");
    fs.mkdirSync(checkout);
    const git = (...args) => execFileSync("git", args, { cwd: checkout, encoding: "utf8", stdio: "pipe" }).trim();
    const run = (mode, before, ...args) =>
      spawnSync(process.execPath, [script, mode, ...args], {
        cwd: checkout,
        encoding: "utf8",
        env: { ...process.env, BEFORE_SHA: before, GITHUB_OUTPUT: "" },
      });
    const project = path.join(checkout, "src/MockAPI/MockAPI.csproj");
    const saveVersion = (version) =>
      fs.writeFileSync(project, `<Project><PropertyGroup><Version>${version}</Version></PropertyGroup></Project>`);
    try {
      git("init", "--bare", "--initial-branch=main", remote);
      git("init", "--initial-branch=main");
      git("config", "user.name", "Release test");
      git("config", "user.email", "release-test@example.invalid");
      git("config", "commit.gpgsign", "false");
      git("config", "tag.gpgsign", "false");
      git("config", "core.hooksPath", path.join(fixture, "no-hooks"));
      git("remote", "add", "origin", remote);
      fs.mkdirSync(path.dirname(project), { recursive: true });
      fs.writeFileSync(path.join(checkout, "package.json"), '{"private":true}');
      saveVersion("1.0.0-beta.1");
      git("add", ".");
      git("commit", "-m", "First fixture");
      const before = git("rev-parse", "HEAD");
      saveVersion("1.0.0-beta.2");
      git("commit", "-am", "Bump fixture");
      git("push", "--set-upstream", "origin", "main");
      const releaseCommit = git("rev-parse", "HEAD");
      const tagged = run("tag", before);
      expect(tagged.stderr).toBe("");
      expect(tagged.status).toBe(0);
      expect(git("rev-parse", "v1.0.0-beta.2^{commit}")).toBe(releaseCommit);
      expect(git("ls-remote", "origin", "refs/tags/v1.0.0-beta.2^{}")).toContain(releaseCommit);
      expect(run("tag", before).stdout).toContain("decision=existing");
      expect(run("resolve", "", "v1.0.0-beta.2").stdout).toContain(`commit=${releaseCommit}`);
      expect(run("resolve", "", "v1.0.0-beta.9").status).toBe(1);
      git("tag", "v1.0.0-beta.4", releaseCommit);
      expect(run("resolve", "", "v1.0.0-beta.4").status).toBe(1);
      git("commit", "--allow-empty", "-m", "Ordinary change");
      expect(run("tag", releaseCommit).stdout).toContain("decision=unchanged");
      expect(git("rev-parse", "v1.0.0-beta.2^{commit}")).toBe(releaseCommit);
      expect(run("tag", "").status).toBe(1);
      fs.writeFileSync(path.join(checkout, "package.json"), '{"private":true,"version":"1.0.0"}');
      expect(run("check", "").status).toBe(1);
    } finally {
      fs.rmSync(fixture, { recursive: true, force: true });
    }
  }, 30000);
});
