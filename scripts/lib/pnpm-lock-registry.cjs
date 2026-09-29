const childProcess = require("node:child_process");
const fs = require("node:fs");

const LOCKFILE_PATH = "pnpm-lock.yaml";
const RESOLUTION = /(resolution:\s*\{)([^}\r\n]*)(\})/g;

function removeRedundantTarballUrls(lockfile) {
  return lockfile.replace(RESOLUTION, (match, prefix, properties, suffix) => {
    if (!/(?:^|,\s*)integrity:/.test(properties) || !/(?:^|,\s*)tarball:\s*https?:\/\//.test(properties)) {
      return match;
    }

    const normalizedProperties = properties
      .replace(/(^|,\s*)tarball:\s*https?:\/\/[^\s,}]+,\s*/, "$1")
      .replace(/,\s*tarball:\s*https?:\/\/[^\s,}]+$/, "");

    return `${prefix}${normalizedProperties}${suffix}`;
  });
}

function normalizeWorkingLockfile({ readFileSync = fs.readFileSync, writeFileSync = fs.writeFileSync } = {}) {
  const workingLockfile = readFileSync(LOCKFILE_PATH, "utf8");
  const normalizedLockfile = removeRedundantTarballUrls(workingLockfile);
  if (normalizedLockfile === workingLockfile) {
    return false;
  }

  writeFileSync(LOCKFILE_PATH, normalizedLockfile);
  return true;
}

function normalizeStagedLockfile({
  execFileSync = childProcess.execFileSync,
  readFileSync = fs.readFileSync,
  writeFileSync = fs.writeFileSync,
} = {}) {
  const stagedLockfile = execFileSync("git", ["show", `:${LOCKFILE_PATH}`], { encoding: "utf8" });
  const normalizedLockfile = removeRedundantTarballUrls(stagedLockfile);
  if (normalizedLockfile === stagedLockfile) {
    return false;
  }

  const objectId = execFileSync("git", ["hash-object", "-w", "--stdin"], {
    encoding: "utf8",
    input: normalizedLockfile,
  }).trim();
  execFileSync("git", ["update-index", "--cacheinfo", `100644,${objectId},${LOCKFILE_PATH}`]);
  normalizeWorkingLockfile({ readFileSync, writeFileSync });
  return true;
}

module.exports = {
  normalizeStagedLockfile,
  normalizeWorkingLockfile,
  removeRedundantTarballUrls,
};
