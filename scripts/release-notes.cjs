const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const { readVersion, validateVersion } = require("./release-version.cjs");

const categories = new Set(["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"]);

function extractReleaseNotes(changelog, version) {
  validateVersion(version);
  const source = changelog.replace(/\r\n/g, "\n");
  const headings = [...source.matchAll(/^## .+$/gm)];
  const matches = headings.filter((heading) => heading[0].startsWith(`## [${version}]`));
  if (matches.length !== 1) {
    throw new Error(`CHANGELOG.md must contain exactly one release entry for ${version}.`);
  }
  const heading = matches[0];
  const date = heading[0].match(/^## \[[^\]]+\] - (\d{4}-\d{2}-\d{2})$/)?.[1];
  const timestamp = date ? Date.parse(`${date}T00:00:00Z`) : NaN;
  if (!Number.isFinite(timestamp) || new Date(timestamp).toISOString().slice(0, 10) !== date) {
    throw new Error(`Changelog entry ${version} must have a valid YYYY-MM-DD release date.`);
  }
  const nextHeading = headings.find((candidate) => candidate.index > heading.index);
  const definitions = [...source.matchAll(/^\[[^\]]+\]: .+$/gm)].map((match) => match[0]);
  const entry = source
    .slice(heading.index + heading[0].length, nextHeading?.index ?? source.length)
    .replace(/^\[[^\]]+\]: .+\n?/gm, "")
    .trim();
  const sections = [...entry.matchAll(/^### (.+)$/gm)];
  if (!sections.length || entry.slice(0, sections[0].index).trim()) {
    throw new Error(`Changelog entry ${version} must contain Keep a Changelog categories.`);
  }
  const seen = new Set();
  for (let index = 0; index < sections.length; index++) {
    const section = sections[index];
    const category = section[1];
    if (!categories.has(category) || seen.has(category)) {
      throw new Error(`Invalid or duplicate changelog category '${category}' for ${version}.`);
    }
    seen.add(category);
    const content = entry.slice(section.index + section[0].length, sections[index + 1]?.index ?? entry.length).trim();
    if (!/^[-*] \S/m.test(content) || /^#{1,6} /m.test(content)) {
      throw new Error(`Changelog category '${category}' for ${version} must contain a nonempty change list.`);
    }
  }
  return `${heading[0]}\n\n${entry}\n${definitions.length ? `\n${definitions.join("\n")}\n` : ""}`;
}

function publishRelease({ repository, tag, commit, notes, verifyOnly = false, run = execFileSync }) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(repository ?? "")) {
    throw new Error("GITHUB_REPOSITORY must identify the release repository.");
  }
  if (!tag?.startsWith("v")) throw new Error("A release tag must use exact v<Version> form.");
  validateVersion(tag.slice(1));
  if (!/^[0-9a-f]{40}$/.test(commit)) throw new Error("A release must target a full Git commit SHA.");
  const prerelease = tag.includes("-");
  const result = run("gh", ["api", "--paginate", "--slurp", `repos/${repository}/releases?per_page=100`], {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  const releases = JSON.parse(result).flat();
  const existing = releases.find((release) => release.tag_name === tag);
  if (existing) {
    if (
      existing.draft ||
      existing.prerelease !== prerelease ||
      existing.target_commitish !== commit ||
      existing.body?.replace(/\r\n/g, "\n").trim() !== notes.trim()
    ) {
      throw new Error(
        `Existing release ${tag} does not match the validated commit and changelog; refusing to replace it.`
      );
    }
    if (verifyOnly && existing.immutable) {
      throw new Error(
        `Release ${tag} is immutable and cannot accept later evidence assets. Resolve the release asset policy before publishing images.`
      );
    }
    return "existing";
  }
  if (verifyOnly) throw new Error(`Release ${tag} is missing. Complete the quality release job first.`);

  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mockapi-release-notes-"));
  try {
    const notesFile = path.join(directory, "release-notes.md");
    fs.writeFileSync(notesFile, notes);
    const options = prerelease ? ["--prerelease", "--latest=false"] : [];
    run(
      "gh",
      [
        "release",
        "create",
        tag,
        "--repo",
        repository,
        "--verify-tag",
        "--target",
        commit,
        "--title",
        `MockAPI ${tag}`,
        "--notes-file",
        notesFile,
        ...options,
      ],
      { stdio: "inherit" }
    );
    return "created";
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
}

function main(mode, requestedTag) {
  if (!["check", "publish", "verify"].includes(mode)) {
    throw new Error("Usage: node scripts/release-notes.cjs check | publish <tag> | verify <tag>");
  }
  const version = readVersion(fs.readFileSync("src/MockAPI/MockAPI.csproj", "utf8"));
  const notes = extractReleaseNotes(fs.readFileSync("CHANGELOG.md", "utf8"), version);
  if (mode === "check") {
    console.log(`Validated Keep a Changelog notes for ${version}.`);
    return;
  }
  if (requestedTag !== `v${version}`) throw new Error("The release tag must match the application version.");
  const git = (...args) => execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
  const commit = git("rev-parse", "HEAD");
  if (git("rev-parse", `refs/tags/${requestedTag}^{commit}`) !== commit) {
    throw new Error("Check out the exact tagged commit before publishing or verifying release notes.");
  }
  const decision = publishRelease({
    repository: process.env.GITHUB_REPOSITORY,
    tag: requestedTag,
    commit,
    notes,
    verifyOnly: mode === "verify",
  });
  console.log(`release=${decision}`);
}

module.exports = { extractReleaseNotes, publishRelease, main };
if (require.main === module) {
  try {
    main(process.argv[2], process.argv[3]);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
