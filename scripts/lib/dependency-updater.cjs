const fs = require("node:fs");
const path = require("node:path");
const { normalizeWorkingLockfile } = require("./pnpm-lock-registry.cjs");
const { runExecutable } = require("./process-runner.cjs");
const { compareVersions, getConfiguredRegistry, getMinimumReleaseAge } = require("./pnpm-updater.cjs");

// Playwright's package version must match the digest-pinned browser image used by CI.
const COUPLED_DEPENDENCIES = new Set(["@playwright/test"]);

function getEligibleStableRelease(name, metadata, minimumReleaseAge, now = new Date()) {
  if (!metadata?.versions || !metadata?.time) {
    throw new Error(`Registry metadata for ${name} must contain versions and publication timestamps.`);
  }

  const cutoff = now.getTime() - minimumReleaseAge * 60_000;
  const candidates = Object.keys(metadata.versions).flatMap((version) => {
    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)) {
      return [];
    }
    if (metadata.versions[version]?.deprecated) {
      return [];
    }

    const published = new Date(metadata.time[version]);
    if (Number.isNaN(published.getTime())) {
      throw new Error(`Missing or invalid publication timestamp for ${name}@${version}.`);
    }
    return published.getTime() <= cutoff ? [{ name, version, published }] : [];
  });

  candidates.sort((left, right) => compareVersions(right.version, left.version));
  if (candidates.length === 0) {
    throw new Error(`No stable ${name} release meets the ${minimumReleaseAge}-minute cooldown.`);
  }
  return candidates[0];
}

function selectDependencyUpdates(
  manifest,
  metadataByName,
  minimumReleaseAge,
  now = new Date(),
  excludedDependencies = COUPLED_DEPENDENCIES
) {
  const updateGroups = {
    dependencies: [],
    devDependencies: [],
  };

  for (const groupName of Object.keys(updateGroups)) {
    for (const [name, currentVersion] of Object.entries(manifest[groupName] ?? {})) {
      if (excludedDependencies.has(name)) {
        continue;
      }
      if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(currentVersion)) {
        throw new Error(`${groupName} entry ${name} must use one exact version.`);
      }

      const release = getEligibleStableRelease(name, metadataByName.get(name), minimumReleaseAge, now);
      const currentIsStable = /^\d+\.\d+\.\d+$/.test(currentVersion);
      if (currentIsStable && compareVersions(currentVersion, release.version) > 0) {
        throw new Error(
          `Pinned ${name}@${currentVersion} is newer than eligible ${release.version}; refusing an automatic downgrade.`
        );
      }
      if (currentVersion !== release.version) {
        updateGroups[groupName].push({ ...release, currentVersion });
      }
    }
  }

  return updateGroups;
}

function applyDependencyUpdates({
  repositoryRoot,
  updateGroups,
  minimumReleaseAge,
  runCommand,
  readFileSync = fs.readFileSync,
  writeFileSync = fs.writeFileSync,
  normalizeLockfile = normalizeWorkingLockfile,
}) {
  const manifestPath = path.join(repositoryRoot, "package.json");
  const lockfilePath = path.join(repositoryRoot, "pnpm-lock.yaml");
  const originalManifest = readFileSync(manifestPath, "utf8");
  const originalLockfile = readFileSync(lockfilePath, "utf8");
  const cooldownArgument = `--config.minimum-release-age=${minimumReleaseAge}`;

  try {
    if (updateGroups.dependencies.length > 0) {
      runCommand(
        "pnpm",
        [
          "add",
          "--save-exact",
          ...updateGroups.dependencies.map((release) => `${release.name}@${release.version}`),
          cooldownArgument,
        ],
        repositoryRoot
      );
    }
    if (updateGroups.devDependencies.length > 0) {
      runCommand(
        "pnpm",
        [
          "add",
          "--save-dev",
          "--save-exact",
          ...updateGroups.devDependencies.map((release) => `${release.name}@${release.version}`),
          cooldownArgument,
        ],
        repositoryRoot
      );
    }
    runCommand("pnpm", ["update", cooldownArgument], repositoryRoot);
    normalizeLockfile({ readFileSync, writeFileSync });
  } catch (error) {
    writeFileSync(manifestPath, originalManifest);
    writeFileSync(lockfilePath, originalLockfile);
    throw error;
  }
}

function runCommand(command, arguments_, cwd = process.cwd()) {
  const capturesOutput = arguments_[0] === "config";
  return runExecutable(command, arguments_, {
    cwd,
    encoding: "utf8",
    stdio: capturesOutput ? ["ignore", "pipe", "inherit"] : "inherit",
  });
}

async function main() {
  const repositoryRoot = path.resolve(__dirname, "..", "..");
  const manifest = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8"));
  const minimumReleaseAge = getMinimumReleaseAge(fs.readFileSync(path.join(repositoryRoot, ".npmrc"), "utf8"));
  const registry = getConfiguredRegistry(runCommand);
  const dependencyNames = [...Object.keys(manifest.dependencies ?? {}), ...Object.keys(manifest.devDependencies ?? {})];
  const metadataEntries = await Promise.all(
    dependencyNames.map(async (name) => {
      const response = await fetch(new URL(encodeURIComponent(name), registry));
      if (!response.ok) {
        throw new Error(`Unable to read metadata for ${name}: HTTP ${response.status}.`);
      }
      return [name, await response.json()];
    })
  );
  const updateGroups = selectDependencyUpdates(manifest, new Map(metadataEntries), minimumReleaseAge);

  for (const release of [...updateGroups.dependencies, ...updateGroups.devDependencies]) {
    console.log(
      `Selected ${release.name}@${release.version}, published ${release.published.toISOString()} ` +
        `(current: ${release.currentVersion}).`
    );
  }
  if (updateGroups.dependencies.length + updateGroups.devDependencies.length === 0) {
    console.log("All direct pnpm dependencies already use the newest eligible stable releases.");
  }

  applyDependencyUpdates({
    repositoryRoot,
    updateGroups,
    minimumReleaseAge,
    runCommand,
  });
}

if (require.main === module) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

module.exports = {
  applyDependencyUpdates,
  COUPLED_DEPENDENCIES,
  getEligibleStableRelease,
  selectDependencyUpdates,
};
