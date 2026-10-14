import { createRequire } from "node:module";
import { describe, expect, it, vi } from "vitest";

const require = createRequire(import.meta.url);
const { verifyQualityEvidence } = require("../../scripts/release-quality.cjs");
const commit = "a".repeat(40);
const tag = "1.0.0";
const candidate = {
  id: 123,
  head_sha: commit,
  head_branch: "main",
  event: "push",
  status: "completed",
  conclusion: "failure",
};
const passedJobs = ["coverage", "browser-smoke", "browser-chromium"].map((name) => ({
  name,
  status: "completed",
  conclusion: "success",
}));

function fixture({ runs = [candidate], jobs = passedJobs } = {}) {
  const run = vi.fn((command, args) => {
    expect(command).toBe("gh");
    expect(args.slice(0, 3)).toEqual(["api", "--paginate", "--slurp"]);
    if (args[3].includes("workflows/quality.yml/runs")) {
      expect(args[3]).toContain(`head_sha=${commit}`);
      expect(args[3]).not.toContain("status=success");
      return JSON.stringify([{ workflow_runs: [] }, { workflow_runs: runs }]);
    }
    return JSON.stringify([{ jobs: jobs.slice(0, 1) }, { jobs: jobs.slice(1) }]);
  });
  return { repository: "example/mockapi", commit, tag, run };
}

describe("release quality prerequisites", () => {
  it.each(["success", "failure", "cancelled"])(
    "accepts a completed %s run only when all quality jobs passed",
    (conclusion) => {
      expect(verifyQualityEvidence(fixture({ runs: [{ ...candidate, conclusion }] }))).toBe(123);
    }
  );

  it.each(["main", tag])("accepts manual validation on %s", (head_branch) => {
    expect(verifyQualityEvidence(fixture({ runs: [{ ...candidate, event: "workflow_dispatch", head_branch }] }))).toBe(
      123
    );
  });

  it.each([
    { event: "pull_request" },
    { event: "release" },
    { head_branch: "feature" },
    { event: "push", head_branch: tag },
    { event: "workflow_dispatch", head_branch: "feature" },
    { head_sha: "b".repeat(40) },
    { status: "in_progress" },
    { status: "queued" },
  ])("rejects unrelated or incomplete evidence %j", (overrides) => {
    const options = fixture({ runs: [{ ...candidate, ...overrides }] });
    expect(() => verifyQualityEvidence(options)).toThrow(`gh workflow run quality.yml --ref ${tag}`);
    expect(options.run).toHaveBeenCalledTimes(1);
  });

  it.each(["coverage", "browser-smoke", "browser-chromium"])("requires successful %s", (name) => {
    for (const conclusion of ["failure", "skipped", "cancelled", null]) {
      const jobs = passedJobs.map((job) => (job.name === name ? { ...job, conclusion } : job));
      expect(() => verifyQualityEvidence(fixture({ jobs }))).toThrow("passed all required jobs");
    }
    expect(() => verifyQualityEvidence(fixture({ jobs: passedJobs.filter((job) => job.name !== name) }))).toThrow(
      "passed all required jobs"
    );
  });

  it("does not accept duplicate or running prerequisite jobs", () => {
    expect(() => verifyQualityEvidence(fixture({ jobs: [...passedJobs, passedJobs[0]] }))).toThrow(
      "passed all required jobs"
    );
    const jobs = passedJobs.map((job) => ({ ...job, status: "in_progress" }));
    expect(() => verifyQualityEvidence(fixture({ jobs }))).toThrow("passed all required jobs");
  });

  it("does not combine partial successes across separate runs", () => {
    const options = fixture({ runs: [candidate, { ...candidate, id: 124 }] });
    options.run.mockImplementation((command, args) => {
      if (args[3].includes("workflows/quality.yml/runs")) {
        return JSON.stringify([{ workflow_runs: [candidate, { ...candidate, id: 124 }] }]);
      }
      const jobs = args[3].includes("/123/") ? passedJobs.slice(0, 1) : passedJobs.slice(1);
      return JSON.stringify([{ jobs }]);
    });
    expect(() => verifyQualityEvidence(options)).toThrow("passed all required jobs");
  });

  it("finds a complete passing run after an incomplete one", () => {
    const options = fixture();
    options.run
      .mockReturnValueOnce(JSON.stringify([{ workflow_runs: [candidate, { ...candidate, id: 124 }] }]))
      .mockReturnValueOnce(JSON.stringify([{ jobs: [] }]))
      .mockReturnValueOnce(JSON.stringify([{ jobs: passedJobs }]));
    expect(verifyQualityEvidence(options)).toBe(124);
  });

  it("reports absence of evidence with the exact immutable-tag recovery command", () => {
    expect(() => verifyQualityEvidence(fixture({ runs: [] }))).toThrow(`gh workflow run quality.yml --ref ${tag}`);
  });

  it.each(["null", "{}", "[]", '[{"workflow_runs":null}]', "not json"])(
    "fails explicitly on an invalid API response %s",
    (response) => {
      const options = fixture();
      options.run.mockReturnValueOnce(response);
      expect(() => verifyQualityEvidence(options)).toThrow();
    }
  );

  it("fails explicitly on an invalid jobs response or run ID", () => {
    const options = fixture();
    options.run
      .mockReturnValueOnce(JSON.stringify([{ workflow_runs: [candidate] }]))
      .mockReturnValueOnce('[{"jobs":null}]');
    expect(() => verifyQualityEvidence(options)).toThrow("Invalid GitHub quality evidence response for jobs");
    expect(() => verifyQualityEvidence(fixture({ runs: [{ ...candidate, id: "../invalid" }] }))).toThrow(
      "Invalid GitHub quality run ID"
    );
  });

  it("propagates scanner authentication and network failures rather than treating them as missing evidence", () => {
    const options = fixture();
    options.run.mockImplementation(() => {
      throw new Error("GitHub API unavailable");
    });
    expect(() => verifyQualityEvidence(options)).toThrow("GitHub API unavailable");
  });

  it.each([{ repository: "" }, { repository: "../invalid/repository" }, { commit: "short" }, { tag: "v1.0.0" }])(
    "rejects invalid evidence inputs %j before querying GitHub",
    (overrides) => {
      const options = { ...fixture(), ...overrides };
      expect(() => verifyQualityEvidence(options)).toThrow();
      expect(options.run).not.toHaveBeenCalled();
    }
  );
});
