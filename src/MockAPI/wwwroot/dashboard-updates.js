"use strict";

/** Tab-session flag that suppresses automatic update notices, not the header update button. @type {string} */
export const DASHBOARD_UPDATE_DISMISSED_KEY = "mockapi.updateDismissed";
const CHECK_INTERVAL_MS = 60 * 60 * 1000;
const REQUEST_TIMEOUT_MS = 15000;
const numericIdentifier = "(?:0|[1-9][0-9]*)";
const prereleaseIdentifier = `(?:${numericIdentifier}|[0-9]*[A-Za-z-][0-9A-Za-z-]*)`;
const versionPattern = new RegExp(
  `^(${numericIdentifier})\\.(${numericIdentifier})\\.(${numericIdentifier})` +
    `(?:-(${prereleaseIdentifier}(?:\\.${prereleaseIdentifier})*))?` +
    "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$"
);

function parseVersion(version) {
  const match = typeof version === "string" ? versionPattern.exec(version) : null;
  if (!match || match[0] !== version) throw new Error("The dashboard release version is not valid SemVer.");
  return { core: match.slice(1, 4), prerelease: match[4] ? match[4].split(".") : [] };
}

function compareNumericIdentifiers(first, second) {
  // Decimal strings preserve SemVer ordering even above JavaScript's safe integer range.
  if (first.length !== second.length) return first.length > second.length ? 1 : -1;
  if (first === second) return 0;
  return first > second ? 1 : -1;
}

/**
 * Compares release precedence, ignoring commit/build metadata and rejecting invalid versions.
 * @param {string} availableVersion Release served by this dashboard instance.
 * @param {string} currentVersion Release embedded in the loaded dashboard HTML.
 * @returns {boolean} True only for a newer release; same-release builds and rollbacks return false.
 * @throws {Error} Either version is not valid SemVer.
 */
export function isNewerDashboardRelease(availableVersion, currentVersion) {
  const available = parseVersion(availableVersion);
  const current = parseVersion(currentVersion);
  for (let index = 0; index < available.core.length; index += 1) {
    const comparison = compareNumericIdentifiers(available.core[index], current.core[index]);
    if (comparison !== 0) return comparison > 0;
  }
  if (available.prerelease.length === 0) return current.prerelease.length > 0;
  if (current.prerelease.length === 0) return false;
  for (let index = 0; index < Math.max(available.prerelease.length, current.prerelease.length); index += 1) {
    const first = available.prerelease[index];
    const second = current.prerelease[index];
    if (first === undefined) return false;
    if (second === undefined) return true;
    if (first === second) continue;
    const firstIsNumeric = /^[0-9]+$/.test(first);
    const secondIsNumeric = /^[0-9]+$/.test(second);
    if (firstIsNumeric && secondIsNumeric) return compareNumericIdentifiers(first, second) > 0;
    if (firstIsNumeric !== secondIsNumeric) return !firstIsNumeric;
    return first > second;
  }
  return false;
}

/**
 * @typedef {object} DashboardUpdateMonitor
 * @property {() => void} start Checks once at startup, then hourly; repeated starts are harmless.
 * @property {(visible: boolean) => void} setVisible Pauses hidden/cached pages, aborts reads, and checks when next due.
 * @property {() => void} dismiss Hides the pending notice and remembers dismissal for this tab's session.
 * @property {() => void} dispose Permanently cancels timers and reads, ignoring late completions.
 */

/**
 * Owns release checks independently of service-worker support and live configuration synchronization.
 * A pending update stops all further checks until the page reloads. Only a dismissal flag enters storage.
 * @param {object} options Monitor dependencies.
 * @param {string} options.currentVersion Release from server-rendered HTML, never an asset hash or build date.
 * @param {(url: string, options: RequestInit) => Promise<Response>} options.request Same-origin fetch adapter.
 * @param {{getItem: (key: string) => string|null, setItem: (key: string, value: string) => void}} options.sessionStorage
 * Guarded storage adapter; its methods must defer access to the browser's sessionStorage getter.
 * @param {(version: string, showNotice: boolean) => void} options.onAvailable Updates the header and nonmodal notice without moving focus.
 * @param {(error: unknown) => void} options.onError Reports failed checks and storage failures; checks retry only at the next hourly interval.
 * @returns {DashboardUpdateMonitor} Page-owned monitor; call dispose on non-persisted pagehide.
 * @throws {Error} The current release version is not valid SemVer.
 */
export function createDashboardUpdateMonitor({ currentVersion, request, sessionStorage, onAvailable, onError }) {
  parseVersion(currentVersion);
  let started = false;
  let visible = true;
  let disposed = false;
  let dismissed = false;
  let nextCheckAt = 0;
  /** @type {string|null} */
  let pendingVersion = null;
  /** @type {ReturnType<typeof globalThis.setTimeout>|null} */
  let timer = null;
  /** @type {{controller: AbortController, timeout: ReturnType<typeof globalThis.setTimeout>}|null} */
  let activeRequest = null;

  function stopWork() {
    if (timer !== null) globalThis.clearTimeout(timer);
    timer = null;
    if (activeRequest) {
      globalThis.clearTimeout(activeRequest.timeout);
      activeRequest.controller.abort();
      activeRequest = null;
    }
  }

  function scheduleCheck() {
    if (!started || !visible || disposed || pendingVersion !== null || activeRequest || timer !== null) return;
    timer = globalThis.setTimeout(
      () => {
        timer = null;
        void check();
      },
      Math.max(0, nextCheckAt - Date.now())
    );
  }

  async function check() {
    if (!visible || disposed || pendingVersion !== null || activeRequest) return;
    if (Date.now() < nextCheckAt) {
      scheduleCheck();
      return;
    }
    nextCheckAt = Date.now() + CHECK_INTERVAL_MS;
    const controller = new AbortController();
    const work = {
      controller,
      timeout: globalThis.setTimeout(
        () => controller.abort(new Error("The dashboard release check timed out.")),
        REQUEST_TIMEOUT_MS
      ),
    };
    activeRequest = work;
    try {
      const response = await request("/__mockapi/version.txt", {
        cache: "no-store",
        credentials: "same-origin",
        redirect: "error",
        signal: controller.signal,
      });
      if (!response.ok) throw new Error(`The dashboard release check failed (HTTP ${response.status}).`);
      const version = (await response.text()).trim();
      // Aborted fetch adapters can still resolve; never publish an obsolete hidden/disposed read.
      if (activeRequest !== work) return;
      if (isNewerDashboardRelease(version, currentVersion)) {
        pendingVersion = version;
        onAvailable(version, !dismissed);
      }
    } catch (error) {
      if (activeRequest === work) onError(error);
    } finally {
      globalThis.clearTimeout(work.timeout);
      if (activeRequest === work) {
        activeRequest = null;
        scheduleCheck();
      }
    }
  }

  return {
    start() {
      if (started || disposed) return;
      started = true;
      try {
        dismissed = sessionStorage.getItem(DASHBOARD_UPDATE_DISMISSED_KEY) === "true";
      } catch (error) {
        onError(
          new Error("Update notice dismissal could not be restored. Browser session storage is unavailable.", {
            cause: error,
          })
        );
      }
      void check();
    },
    setVisible(value) {
      if (disposed || visible === value) return;
      visible = value;
      stopWork();
      if (visible && started) void check();
    },
    dismiss() {
      if (disposed || pendingVersion === null) return;
      dismissed = true;
      try {
        sessionStorage.setItem(DASHBOARD_UPDATE_DISMISSED_KEY, "true");
      } catch (error) {
        onError(
          new Error("Update notice dismissal applies only to this page. Browser session storage is unavailable.", {
            cause: error,
          })
        );
      }
      onAvailable(pendingVersion, false);
    },
    dispose() {
      disposed = true;
      stopWork();
    },
  };
}
