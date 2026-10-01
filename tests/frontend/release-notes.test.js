import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const { extractReleaseNotes, publishRelease } = require("../../scripts/release-notes.cjs");
const repositoryRoot = path.resolve(import.meta.dirname, "..", "..");
const commit = "a".repeat(40);
const version = "1.2.3";
const entry = `## [${version}] - 2026-10-01\n\n### Added\n\n- A reviewed feature.\n\n### Fixed\n\n- A regression.\n`;
const notes = extractReleaseNotes(`# Changelog\n\n${entry}`, version);
const release = {
  tag_name: `v${version}`,
  target_commitish: commit,
  prerelease: false,
  draft: false,
  body: notes,
};
const candidate = { repository: "owner/mockapi", tag: `v${version}`, commit, notes };

describe("Keep a Changelog release notes", () => {
  it("extracts only the requested version, preserving Markdown and link definitions", () => {
    const definitions = "[1.2.3]: https://example.invalid/compare/v1.2.2...v1.2.3\n";
    const changelog = `# Changelog\n\n## [Unreleased]\n\n### Added\n\n- Future work.\n\n${entry}\n## [1.2.2] - 2026-09-01\n\n### Fixed\n\n- Old work.\n\n${definitions}`;

    expect(extractReleaseNotes(changelog.replace(/\n/g, "\r\n"), version)).toBe(`${entry}\n${definitions}`);
    expect(notes).toBe(entry);
  });

  it.each(["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"])("supports %s", (category) => {
    const changelog = `## [${version}] - 2026-10-01\n\n### ${category}\n\n- Reviewed change.\n`;
    expect(extractReleaseNotes(changelog, version)).toBe(changelog);
  });

  it.each([
    ["missing entry", "# Changelog", "exactly one release entry"],
    ["duplicate entry", `${entry}\n${entry}`, "exactly one release entry"],
    ["undated entry", entry.replace(" - 2026-10-01", ""), "valid YYYY-MM-DD"],
    ["invalid day", entry.replace("2026-10-01", "2026-02-30"), "valid YYYY-MM-DD"],
    ["invalid month", entry.replace("2026-10-01", "2026-13-01"), "valid YYYY-MM-DD"],
    ["missing categories", `## [${version}] - 2026-10-01\n\n- Change.`, "Keep a Changelog categories"],
    ["uncategorized changes", entry.replace("### Added", "- Uncategorized.\n\n### Added"), "categories"],
    ["unknown category", entry.replace("### Added", "### Features"), "Invalid or duplicate"],
    ["duplicate category", entry.replace("### Fixed", "### Added"), "Invalid or duplicate"],
    ["empty category", entry.replace("- A reviewed feature.", ""), "nonempty change list"],
    ["prose instead of list", entry.replace("- A reviewed feature.", "A reviewed feature."), "nonempty change list"],
    ["nested heading", entry.replace("- A reviewed feature.", "#### Feature\n\n- Change."), "nonempty change list"],
  ])("rejects %s", (_scenario, changelog, message) => {
    expect(() => extractReleaseNotes(changelog, version)).toThrow(message);
  });

  it("validates the checked-in application's reviewed entry", () => {
    const { readVersion } = require("../../scripts/release-version.cjs");
    const currentVersion = readVersion(
      fs.readFileSync(path.join(repositoryRoot, "src", "MockAPI", "MockAPI.csproj"), "utf8")
    );
    expect(
      extractReleaseNotes(fs.readFileSync(path.join(repositoryRoot, "CHANGELOG.md"), "utf8"), currentVersion)
    ).toContain(`## [${currentVersion}] - `);
  });
});

describe("automatic GitHub releases", () => {
  it.each([
    { tag: "v1.2.3", prerelease: false },
    { tag: "v1.3.0-beta.1", prerelease: true },
  ])("creates $tag from reviewed notes at the exact commit", ({ tag, prerelease }) => {
    let temporaryFile;
    const run = vi.fn((executable, args) => {
      expect(executable).toBe("gh");
      if (args[0] === "api") return JSON.stringify([[], []]);
      temporaryFile = args[args.indexOf("--notes-file") + 1];
      expect(fs.readFileSync(temporaryFile, "utf8")).toBe(notes);
      expect(args.slice(0, 3)).toEqual(["release", "create", tag]);
      expect(args).toContain("--verify-tag");
      expect(args[args.indexOf("--target") + 1]).toBe(commit);
      expect(args[args.indexOf("--repo") + 1]).toBe(candidate.repository);
      expect(args[args.indexOf("--title") + 1]).toBe(`MockAPI ${tag}`);
      expect(args.includes("--prerelease")).toBe(prerelease);
      expect(args.includes("--latest=false")).toBe(prerelease);
      expect(args).not.toContain("--generate-notes");
      return "";
    });

    expect(publishRelease({ ...candidate, tag, run })).toBe("created");
    expect(run).toHaveBeenCalledTimes(2);
    expect(fs.existsSync(temporaryFile)).toBe(false);
  });

  it("leaves matching published releases and their assets untouched on rerun", () => {
    const run = vi.fn(() => JSON.stringify([[{ tag_name: "v1.2.2" }], [release]]));
    expect(publishRelease({ ...candidate, run })).toBe("existing");
    expect(run).toHaveBeenCalledTimes(1);
    expect(run.mock.calls[0][1]).toContain("--paginate");
  });

  it.each([
    ["draft", { draft: true }],
    ["wrong stability", { prerelease: true }],
    ["wrong target", { target_commitish: "b".repeat(40) }],
    ["different notes", { body: "Existing notes must not be overwritten." }],
    ["missing notes", { body: null }],
  ])("refuses an existing release with %s", (_scenario, overrides) => {
    const run = vi.fn(() => JSON.stringify([[{ ...release, ...overrides }]]));
    expect(() => publishRelease({ ...candidate, run })).toThrow("refusing to replace");
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("verifies the existing release before approved container publication", () => {
    const run = vi.fn(() => JSON.stringify([[release]]));
    expect(publishRelease({ ...candidate, run, verifyOnly: true })).toBe("existing");
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("does not create a missing release during container verification", () => {
    const run = vi.fn(() => JSON.stringify([[]]));
    expect(() => publishRelease({ ...candidate, run, verifyOnly: true })).toThrow("Complete the quality release job");
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("rejects immutable releases before images can be published with unattached evidence", () => {
    const run = vi.fn(() => JSON.stringify([[{ ...release, immutable: true }]]));
    expect(() => publishRelease({ ...candidate, run, verifyOnly: true })).toThrow(
      "cannot accept later evidence assets"
    );
    expect(run).toHaveBeenCalledTimes(1);
    expect(publishRelease({ ...candidate, run })).toBe("existing");
  });

  it.each(["authentication failure", "network failure", "rate limited"])(
    "surfaces %s without creating a release",
    (message) => {
      const run = vi.fn(() => {
        throw new Error(message);
      });
      expect(() => publishRelease({ ...candidate, run })).toThrow(message);
      expect(run).toHaveBeenCalledTimes(1);
    }
  );

  it("surfaces a publication failure and cleans up temporary notes", () => {
    let temporaryFile;
    const run = vi.fn((_executable, args) => {
      if (args[0] === "api") return JSON.stringify([[]]);
      temporaryFile = args[args.indexOf("--notes-file") + 1];
      throw new Error("Publication failed");
    });
    expect(() => publishRelease({ ...candidate, run })).toThrow("Publication failed");
    expect(fs.existsSync(temporaryFile)).toBe(false);
  });

  it.each([
    { repository: "" },
    { repository: "owner/mockapi;unsafe" },
    { tag: "1.2.3" },
    { tag: "v1.2.3;unsafe" },
    { commit: "main" },
  ])("rejects invalid release inputs before invoking GitHub: %j", (overrides) => {
    const run = vi.fn();
    expect(() => publishRelease({ ...candidate, ...overrides, run })).toThrow();
    expect(run).not.toHaveBeenCalled();
  });
});

describe("release workflow wiring", () => {
  it("publishes only eligible validated main versions and validates notes before tagging", () => {
    const quality = fs.readFileSync(path.join(repositoryRoot, ".github", "workflows", "quality.yml"), "utf8");
    const job = quality.slice(quality.indexOf("  version-tag:"), quality.indexOf("  browser-chromium:"));
    expect(job).toContain("needs: [coverage, browser-smoke, browser-chromium]");
    expect(job).toContain("github.ref == 'refs/heads/main'");
    expect(job).toContain("(github.event_name == 'push' || github.event_name == 'workflow_dispatch')");
    expect(job).toContain("if: steps.version.outputs.decision != 'unchanged'");
    expect(job).toContain('node scripts/release-notes.cjs publish "$RELEASE_TAG"');
    expect(quality.match(/- "CHANGELOG.md"/g)).toHaveLength(2);
    expect(quality).toContain("node scripts/release-notes.cjs check");
  });

  it("keeps image publication approved and attaches evidence without replacing notes or assets", () => {
    const workflow = fs.readFileSync(
      path.join(repositoryRoot, ".github", "workflows", "container-release.yml"),
      "utf8"
    );
    expect(workflow).toContain("workflow_dispatch:");
    expect(workflow).toContain("ENABLE_RELEASE_PUBLISHING");
    expect(workflow).toContain("approve_publication");
    expect(workflow).toContain("environment: release");
    expect(workflow).toContain('node scripts/release-notes.cjs verify "$IMAGE_TAG"');
    expect(workflow.indexOf("release-notes.cjs verify")).toBeLessThan(workflow.indexOf("docker login"));
    expect(workflow).toContain('gh release upload "$IMAGE_TAG" --repo "$GITHUB_REPOSITORY"');
    expect(workflow).not.toContain("gh release create");
    expect(workflow).not.toContain("--generate-notes");
    expect(workflow).not.toContain("--clobber");
  });
});
