const fs = require("node:fs");
const path = require("node:path");
const { runExecutable } = require("./process-runner.cjs");

const DEFAULT_REGISTRY = "https://registry.npmjs.org/";

function getMinimumReleaseAge(npmrc) {
  const setting = npmrc
    .split(/\r?\n/)
    .map((line) => line.trim())
    .find((line) => line.startsWith("minimum-release-age="));
  const configuredValue = setting?.split("=", 2)[1] ?? "";
  if (!/^[1-9]\d*$/.test(configuredValue)) {
    throw new Error("Expected a positive minimum-release-age in .npmrc.");
  }
  return Number.parseInt(configuredValue, 10);
}

function compareVersions(left, right) {
  const leftParts = left.split(".").map(Number);
  const rightParts = right.split(".").map(Number);
  for (let index = 0; index < leftParts.length; index += 1) {
    if (leftParts[index] !== rightParts[index]) {
      return leftParts[index] - rightParts[index];
    }
  }
  return 0;
}

function getEligiblePnpmRelease(metadata, minimumReleaseAge, now = new Date()) {
  if (!metadata?.versions || !metadata?.time) {
    throw new Error("The npm registry response must contain versions and publication timestamps.");
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
      throw new Error(`Missing or invalid publication timestamp for pnpm ${version}.`);
    }
    return published.getTime() <= cutoff ? [{ version, published }] : [];
  });

  candidates.sort((left, right) => compareVersions(right.version, left.version));
  if (candidates.length === 0) {
    throw new Error(`No stable pnpm release meets the ${minimumReleaseAge}-minute cooldown.`);
  }
  return candidates[0];
}

function setPnpmPins(manifest, version) {
  const packageManagerPattern = /("packageManager"\s*:\s*")pnpm@[^"]+(")/;
  const enginePattern = /("engines"\s*:\s*\{[^{}]*?"pnpm"\s*:\s*")[^"]+(")/;
  if (!packageManagerPattern.test(manifest) || !enginePattern.test(manifest)) {
    throw new Error("Expected pnpm packageManager and engines pins in package.json.");
  }

  return manifest.replace(packageManagerPattern, `$1pnpm@${version}$2`).replace(enginePattern, `$1${version}$2`);
}

function getConfiguredRegistry(runCommand) {
  const configured = String(runCommand("pnpm", ["config", "get", "registry"])).trim();
  const registry = configured && !["undefined", "null"].includes(configured) ? configured : DEFAULT_REGISTRY;
  let registryUrl;
  try {
    registryUrl = new URL(registry);
  } catch {
    throw new Error("The configured npm registry must be an absolute HTTP(S) URL without a query or fragment.");
  }
  if (!["http:", "https:"].includes(registryUrl.protocol) || registryUrl.search || registryUrl.hash) {
    throw new Error("The configured npm registry must be an absolute HTTP(S) URL without a query or fragment.");
  }
  return registryUrl;
}

function updatePnpm({
  repositoryRoot,
  metadata,
  runCommand,
  now = new Date(),
  readFileSync = fs.readFileSync,
  writeFileSync = fs.writeFileSync,
}) {
  const minimumAge = getMinimumReleaseAge(readFileSync(path.join(repositoryRoot, ".npmrc"), "utf8"));
  const release = getEligiblePnpmRelease(metadata, minimumAge, now);
  const manifestPath = path.join(repositoryRoot, "package.json");
  const originalManifest = readFileSync(manifestPath, "utf8");
  const currentVersion = JSON.parse(originalManifest).packageManager?.match(/^pnpm@(\d+\.\d+\.\d+)$/)?.[1];
  if (!currentVersion) {
    throw new Error("Expected an exact pnpm packageManager pin in package.json.");
  }
  if (compareVersions(currentVersion, release.version) > 0) {
    throw new Error(
      `Pinned pnpm ${currentVersion} is newer than eligible ${release.version}; refusing an automatic downgrade.`
    );
  }

  const previousCi = process.env.CI;
  try {
    process.env.CI = "true";
    runCommand("pnpm", ["self-update", release.version, `--config.minimum-release-age=${minimumAge}`], repositoryRoot);
    writeFileSync(manifestPath, setPnpmPins(readFileSync(manifestPath, "utf8"), release.version));
    const installedVersion = String(runCommand("pnpm", ["--version"], repositoryRoot)).trim();
    if (installedVersion !== release.version) {
      throw new Error(`Expected pnpm ${release.version}, but the active project version is '${installedVersion}'.`);
    }
  } catch (error) {
    writeFileSync(manifestPath, originalManifest);
    throw error;
  } finally {
    if (previousCi === undefined) {
      delete process.env.CI;
    } else {
      process.env.CI = previousCi;
    }
  }

  return { ...release, minimumAge };
}

function runCommand(command, arguments_, cwd = process.cwd()) {
  const capturesOutput = arguments_[0] === "--version" || arguments_[0] === "config";
  return runExecutable(command, arguments_, {
    cwd,
    encoding: "utf8",
    stdio: capturesOutput ? ["ignore", "pipe", "inherit"] : "inherit",
  });
}

async function main() {
  const repositoryRoot = path.resolve(__dirname, "..", "..");
  const registry = getConfiguredRegistry(runCommand);
  const response = await fetch(new URL("pnpm", registry));
  if (!response.ok) {
    throw new Error(`Unable to read pnpm metadata from the configured registry: HTTP ${response.status}.`);
  }
  const metadata = await response.json();
  const release = updatePnpm({ repositoryRoot, metadata, runCommand });
  console.log(
    `Project pnpm is now ${release.version}, published ${release.published.toISOString()}; ` +
      `cooldown: ${release.minimumAge} minutes.`
  );
}

if (require.main === module) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

module.exports = {
  compareVersions,
  getConfiguredRegistry,
  getEligiblePnpmRelease,
  getMinimumReleaseAge,
  setPnpmPins,
  updatePnpm,
};
