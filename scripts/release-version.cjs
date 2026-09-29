const fs = require("node:fs");
const { execFileSync, spawnSync } = require("node:child_process");

const projectPath = "src/MockAPI/MockAPI.csproj";
const number = "(?:0|[1-9][0-9]*)";
const identifier = `(?:${number}|[0-9]*[A-Za-z-][0-9A-Za-z-]*)`;
const versionPattern = new RegExp(`^${number}\\.${number}\\.${number}(?:-${identifier}(?:\\.${identifier})*)?$`);

function validateVersion(version) {
  if (!versionPattern.test(version) || version.trim() !== version || version.length > 127) {
    throw new Error(
      `Invalid application version '${version}': use MAJOR.MINOR.PATCH with an optional SemVer prerelease, without build metadata.`
    );
  }
  return version;
}

function readVersion(project) {
  const source = project.replace(/<!--[\s\S]*?-->/g, "");
  const declarations = [...source.matchAll(/<Version>([^<]+)<\/Version>/g)];
  const versionElements = [...source.matchAll(/<Version\b/g)];
  const conditionalGroup = /<PropertyGroup\b[^>]*\bCondition\s*=[^>]*>(?:(?!<\/PropertyGroup>)[\s\S])*<Version\b/.test(
    source
  );
  if (declarations.length !== 1 || versionElements.length !== 1 || conditionalGroup) {
    throw new Error("The application project must declare exactly one unconditional <Version>.");
  }
  return validateVersion(declarations[0][1].trim());
}

function compareVersions(left, right) {
  const split = (version) => {
    validateVersion(version);
    const separator = version.indexOf("-");
    return {
      core: (separator < 0 ? version : version.slice(0, separator)).split(".").map(BigInt),
      prerelease: separator < 0 ? [] : version.slice(separator + 1).split("."),
    };
  };
  const a = split(left);
  const b = split(right);
  for (let index = 0; index < 3; index++) {
    if (a.core[index] !== b.core[index]) return a.core[index] > b.core[index] ? 1 : -1;
  }
  if (!a.prerelease.length || !b.prerelease.length) {
    return Math.sign(b.prerelease.length) - Math.sign(a.prerelease.length);
  }
  for (let index = 0; index < Math.max(a.prerelease.length, b.prerelease.length); index++) {
    const first = a.prerelease[index];
    const second = b.prerelease[index];
    if (first === second) continue;
    if (first === undefined) return -1;
    if (second === undefined) return 1;
    const firstNumeric = /^[0-9]+$/.test(first);
    const secondNumeric = /^[0-9]+$/.test(second);
    if (firstNumeric && secondNumeric) return BigInt(first) > BigInt(second) ? 1 : -1;
    if (firstNumeric !== secondNumeric) return firstNumeric ? -1 : 1;
    return first > second ? 1 : -1;
  }
  return 0;
}

function planTag({ version, previousVersion, head, existingCommit }) {
  validateVersion(version);
  if (previousVersion === version) return "unchanged";
  if (previousVersion && compareVersions(version, previousVersion) <= 0) {
    throw new Error(`Version ${version} must be newer than ${previousVersion}.`);
  }
  if (existingCommit && existingCommit !== head) {
    throw new Error(`Refusing to move existing tag v${version}. Select a new version.`);
  }
  return existingCommit ? "existing" : "create";
}

function git(...args) {
  return execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
}

function tagCommit(tag) {
  const result = spawnSync("git", ["show-ref", "--verify", "--quiet", `refs/tags/${tag}`]);
  if (result.error) throw result.error;
  if (result.status === 1) return null;
  if (result.status !== 0) throw new Error(`Cannot inspect tag ${tag}: ${result.stderr}`);
  return git("rev-parse", `refs/tags/${tag}^{commit}`);
}

function emit(metadata) {
  for (const [key, value] of Object.entries(metadata)) {
    console.log(`${key}=${value}`);
    if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `${key}=${value}\n`);
  }
}

function main(mode, requestedTag) {
  if (mode === "resolve") {
    if (!requestedTag?.startsWith("v")) throw new Error("A release tag must use exact v<Version> form.");
    validateVersion(requestedTag.slice(1));
    const commit = tagCommit(requestedTag);
    if (!commit) throw new Error(`Tag ${requestedTag} does not exist. Run quality validation and tagging first.`);
    const version = readVersion(git("show", `${commit}:${projectPath}`));
    if (requestedTag !== `v${version}`) throw new Error("The release tag does not match its project's version.");
    git("merge-base", "--is-ancestor", commit, "origin/main");
    emit({ version, tag: requestedTag, commit, prerelease: version.includes("-") });
    return;
  }

  const version = readVersion(fs.readFileSync(projectPath, "utf8"));
  const tooling = JSON.parse(fs.readFileSync("package.json", "utf8"));
  if (tooling.private !== true || Object.hasOwn(tooling, "version")) {
    throw new Error("package.json must remain private, unversioned development tooling.");
  }
  if (mode === "check") {
    if (requestedTag && requestedTag !== `v${version}`) throw new Error("The tag must match the application version.");
    emit({ version, tag: `v${version}`, prerelease: version.includes("-") });
    return;
  }
  if (mode !== "tag") throw new Error("Usage: node scripts/release-version.cjs check [tag] | resolve <tag> | tag");

  const before = process.env.BEFORE_SHA;
  if (before && !/^[0-9a-f]{40}$/.test(before)) throw new Error("BEFORE_SHA must be a full Git commit SHA.");
  const previousVersion = before && !/^0+$/.test(before) ? readVersion(git("show", `${before}:${projectPath}`)) : null;
  const head = git("rev-parse", "HEAD");
  const tag = `v${version}`;
  const decision = planTag({ version, previousVersion, head, existingCommit: tagCommit(tag) });
  if (decision === "create") {
    git(
      "-c",
      "user.name=github-actions[bot]",
      "-c",
      "user.email=41898282+github-actions[bot]@users.noreply.github.com",
      "tag",
      "--annotate",
      tag,
      head,
      "--message",
      `MockAPI ${version}`
    );
    // No force: a concurrent or previously published tag must never be replaced.
    git("push", "origin", `refs/tags/${tag}`);
  }
  emit({ version, tag, decision });
}

module.exports = { validateVersion, readVersion, compareVersions, planTag, main };
if (require.main === module) {
  try {
    main(process.argv[2], process.argv[3]);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
