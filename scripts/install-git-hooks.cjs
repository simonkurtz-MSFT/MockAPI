const childProcess = require("node:child_process");

function installGitHooks({
  environment = process.env,
  runGit = childProcess.execFileSync,
  writeOutput = console.log,
} = {}) {
  if (environment.CI) {
    writeOutput("Skipping repository Git hook installation in CI.");
    return false;
  }

  runGit("git", ["config", "core.hooksPath", ".githooks"], {
    stdio: "inherit",
  });
  return true;
}

if (require.main === module) {
  installGitHooks();
}

module.exports = {
  installGitHooks,
};
