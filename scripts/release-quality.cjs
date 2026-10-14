const { execFileSync } = require("node:child_process");
const { validateVersion } = require("./release-version.cjs");

const requiredJobs = ["coverage", "browser-smoke", "browser-chromium"];

function verifyQualityEvidence({ repository, commit, tag, run = execFileSync }) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(repository ?? "")) {
    throw new Error("GITHUB_REPOSITORY must identify the release repository.");
  }
  if (!/^[0-9a-f]{40}$/.test(commit ?? "")) {
    throw new Error("Quality evidence must target a full Git commit SHA.");
  }
  validateVersion(tag);

  function readPages(endpoint, collection) {
    const result = run("gh", ["api", "--paginate", "--slurp", endpoint], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    const pages = JSON.parse(result);
    if (!Array.isArray(pages) || !pages.length || pages.some((page) => !Array.isArray(page?.[collection]))) {
      throw new Error(`Invalid GitHub quality evidence response for ${collection}.`);
    }
    return pages.flatMap((page) => page[collection]);
  }

  const runs = readPages(
    `repos/${repository}/actions/workflows/quality.yml/runs?head_sha=${commit}&per_page=100`,
    "workflow_runs"
  );
  for (const candidate of runs) {
    const approvedEvent =
      (candidate.event === "push" && candidate.head_branch === "main") ||
      (candidate.event === "workflow_dispatch" && ["main", tag].includes(candidate.head_branch));
    if (candidate.head_sha !== commit || candidate.status !== "completed" || !approvedEvent) continue;
    if (!Number.isSafeInteger(candidate.id) || candidate.id <= 0) {
      throw new Error("Invalid GitHub quality run ID.");
    }
    const jobs = readPages(`repos/${repository}/actions/runs/${candidate.id}/jobs?per_page=100`, "jobs");
    // Publication can fail after quality passes. Require every prerequisite in one
    // completed run, not a successful overall run or jobs combined across runs.
    const qualityPassed = requiredJobs.every((name) => {
      const matches = jobs.filter((job) => job.name === name);
      return matches.length === 1 && matches[0].status === "completed" && matches[0].conclusion === "success";
    });
    if (qualityPassed) return candidate.id;
  }
  throw new Error(
    `No completed Quality engineering run passed all required jobs for release commit ${commit}. ` +
      `Run 'gh workflow run quality.yml --ref ${tag}', wait for it to succeed, then retry this workflow.`
  );
}

module.exports = { verifyQualityEvidence };
if (require.main === module) {
  try {
    const runId = verifyQualityEvidence({
      repository: process.env.GITHUB_REPOSITORY,
      commit: process.env.RELEASE_COMMIT,
      tag: process.env.RELEASE_TAG,
    });
    console.log(`Quality prerequisites passed in run ${runId}.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
