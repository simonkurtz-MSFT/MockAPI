"use strict";

/**
 * @typedef {RequestInit & {mutatesConfiguration?: boolean, etag?: string|null, expectedEtag?: string}} ManagementRequestOptions
 * Configuration writes require the revision captured when the user began the operation.
 * Endpoint reads can require an exact response revision before consuming the body.
 */

/**
 * @typedef {<T = unknown>(path: string, options?: ManagementRequestOptions) => Promise<T>} ManagementClient
 * Typed JSON boundary for server-validated contracts; callers select the expected payload type.
 * A 204 resolves to null. HTTP, network, cancellation, and malformed-JSON errors reject.
 */

/** @typedef {{kind: "completed"|"failed"|"busy"}} CommandResult */
/** @typedef {(error: import("./dashboard-core.js").ManagementError) => void} ReportManagementError */

/**
 * @typedef {object} DashboardCommandRunner
 * @property {(action: () => Promise<void>|void, reportError?: ReportManagementError) => Promise<CommandResult>} run
 * Runs one command through its authoritative refresh. The optional presenter handles action/busy errors;
 * refresh failures always reach the page-level presenter. Neither writes nor busy submissions are replayed.
 */

/**
 * Creates the dashboard's non-storing management transport. It never retries writes.
 *
 * @param {{request: (url: string, options: RequestInit) => Promise<Response>}} dependencies Injected fetch implementation.
 * @returns {ManagementClient} JSON request function; result types describe the server contract, not client-side validation.
 * @throws {Error} Missing write revision, mismatched read revision, or HTTP failure (with status and problem properties).
 * Network, cancellation, and invalid successful JSON errors propagate unchanged.
 */
export function createDashboardManagementClient({ request }) {
  /**
   * @template [T=unknown]
   * @param {string} path Management-relative request path.
   * @param {ManagementRequestOptions} options Request and revision constraints.
   * @returns {Promise<T>} Deserialized server payload.
   */
  return async function api(path, options = {}) {
    const { mutatesConfiguration = false, etag, expectedEtag, ...requestOptions } = options;
    const headers = new Headers(requestOptions.headers);
    if (requestOptions.body !== undefined) headers.set("Content-Type", "application/json");
    if (mutatesConfiguration) {
      if (typeof etag !== "string" || !/^"\d+"$/.test(etag)) {
        throw new Error("A configuration revision is required. Wait for synchronization and reopen the operation.");
      }
      headers.set("If-Match", etag);
    }
    const response = await request(`/__mockapi/api${path}`, { ...requestOptions, headers, cache: "no-store" });
    if (!response.ok) {
      /** @type {import("./dashboard-core.js").ManagementProblem} */
      let problem = {};
      try {
        const body = await response.json();
        if (body !== null && typeof body === "object") problem = body;
      } catch (error) {
        // Non-JSON HTTP errors still have a useful status; cancellation and read failures must propagate.
        if (!(error instanceof SyntaxError)) throw error;
      }
      /** @type {import("./dashboard-core.js").ManagementError} */
      const error = new Error(problem.detail || `${response.status} ${response.statusText}`);
      error.problem = problem;
      error.status = response.status;
      throw error;
    }
    if (expectedEtag !== undefined && response.headers.get("ETag") !== expectedEtag) {
      throw new Error("Configuration changed while endpoints were loading; resynchronizing.");
    }
    if (response.status === 204) return null;
    return response.json();
  };
}

/**
 * Owns one management command at a time, including its authoritative refresh.
 * Failures are presented explicitly; neither failed nor busy commands are retried.
 *
 * @param {object} dependencies Lifecycle callbacks.
 * @param {() => Promise<void>} dependencies.synchronize Refreshes authoritative dashboard state after success or failure.
 * @param {ReportManagementError} dependencies.onError Presents command, busy, and refresh errors.
 * @param {(pending: boolean) => void} dependencies.onPendingChange Updates command controls on entry and exit.
 * @returns {DashboardCommandRunner} Single-flight command owner; pending state is released after refresh.
 */
export function createDashboardCommandRunner({ synchronize, onError, onPendingChange }) {
  let pending = false;

  /** @type {DashboardCommandRunner["run"]} */
  async function run(action, reportError = onError) {
    if (pending) {
      reportError(new Error("A management operation is already in progress. Wait for it to finish."));
      return { kind: "busy" };
    }
    pending = true;
    try {
      onPendingChange(true);
      /** @type {CommandResult["kind"]} */
      let kind = "completed";
      try {
        await action();
      } catch (error) {
        kind = "failed";
        reportError(error);
      }
      try {
        await synchronize();
      } catch (error) {
        kind = "failed";
        onError(error);
      }
      return { kind };
    } finally {
      pending = false;
      onPendingChange(false);
    }
  }

  return { run };
}
