"use strict";

/** Storage key for the versioned dashboard preference document. @type {string} */
export const DASHBOARD_PREFERENCES_KEY = "mockapi.preferences";
/** Session-only storage key for collapsed endpoint API groups. @type {string} */
export const ENDPOINT_GROUP_SESSION_KEY = "mockapi.endpointGroups";
const LEGACY_THEME_KEY = "mockapi.theme";
const VERSION = 1;

/** @typedef {"auto"|"columns"|"stacked"} DashboardLayout */
/** @typedef {"left"|"center"|"right"} TestBladeAlignment */
/** @typedef {"raw"|"pretty"} ResponseBodyPresentation */
/** @typedef {"graph"|"table"} StatisticsView */
/** @typedef {"utc"|"local"} RequestLogTimeZone */

/**
 * @typedef {Object} DashboardPreferences
 * @property {"dark"|"light"|null} theme Explicit theme, or `null` to follow the operating system.
 * @property {DashboardLayout} dashboardLayout Preferred dashboard workspace layout.
 * @property {TestBladeAlignment} endpointTestDialogAlignment Preferred endpoint test dialog alignment.
 * @property {boolean} endpointsCollapsed Whether the endpoint section is collapsed.
 * @property {boolean} rememberEndpointGroups Whether API-group collapse state survives refreshes in this tab's session.
 * @property {boolean} kjUiStyle Whether the dashboard uses hard corners throughout.
 * @property {boolean} requestLogCollapsed Whether the request log section is collapsed.
 * @property {boolean} showUnexpectedRequests Whether unexpected dispatch outcomes appear in the request log.
 * @property {boolean} showCurlAction Whether endpoint rows offer a cURL clipboard action.
 * @property {RequestLogTimeZone} requestLogTimeZone Time zone used for request-log entries and minute labels; defaults to UTC.
 * @property {ResponseBodyPresentation} responseBodyPresentation Preferred endpoint test response presentation when pretty printing is available.
 * @property {StatisticsView} statisticsView Selected statistics presentation.
 * @property {boolean} statisticsCollapsed Whether the statistics section is collapsed.
 * @property {boolean} tutorialDismissed Whether the first-run dashboard tour has been dismissed.
 */

/**
 * @typedef {Object} DashboardPreferencesStore
 * @property {() => Readonly<DashboardPreferences>} get Gets the immutable active preferences.
 * @property {(changes: Partial<DashboardPreferences>) => Readonly<DashboardPreferences>} update Normalizes, applies, and best-effort persists only view preferences. Storage failures leave the in-memory update active.
 */

const isTheme = (value) => value === "dark" || value === "light";

/** @returns {Readonly<DashboardPreferences>} Validated, frozen view preferences. */
function normalizePreferences(value, legacyTheme = null) {
  const supported = value && (value.version === undefined || value.version === VERSION) ? value : {};
  return Object.freeze({
    theme: isTheme(supported.theme) ? supported.theme : isTheme(legacyTheme) ? legacyTheme : null,
    dashboardLayout: ["auto", "columns", "stacked"].includes(supported.dashboardLayout)
      ? supported.dashboardLayout
      : "auto",
    endpointTestDialogAlignment: ["left", "center", "right"].includes(supported.endpointTestDialogAlignment)
      ? supported.endpointTestDialogAlignment
      : "right",
    endpointsCollapsed: typeof supported.endpointsCollapsed === "boolean" ? supported.endpointsCollapsed : false,
    rememberEndpointGroups: supported.rememberEndpointGroups !== false,
    kjUiStyle: supported.kjUiStyle === true,
    requestLogCollapsed: typeof supported.requestLogCollapsed === "boolean" ? supported.requestLogCollapsed : false,
    showUnexpectedRequests: supported.showUnexpectedRequests !== false,
    showCurlAction: supported.showCurlAction !== false,
    requestLogTimeZone: supported.requestLogTimeZone === "local" ? "local" : "utc",
    responseBodyPresentation: supported.responseBodyPresentation === "pretty" ? "pretty" : "raw",
    statisticsView: ["graph", "table"].includes(supported.statisticsView) ? supported.statisticsView : "graph",
    statisticsCollapsed: typeof supported.statisticsCollapsed === "boolean" ? supported.statisticsCollapsed : false,
    tutorialDismissed: supported.tutorialDismissed === true,
  });
}

/**
 * @typedef {object} EndpointGroupSessionStore
 * @property {() => string[]} read Reads collapsed group identities; invalid or unavailable storage reports an error and returns an empty list.
 * @property {(groups: readonly string[]|null) => void} write Saves collapsed group identities, or removes them when null; storage failures are reported without changing page state.
 */

/**
 * Persists only API-group identities in tab-scoped session storage, never endpoint definitions or request data.
 * Storage access stays inside guarded adapter calls because the sessionStorage getter itself can throw.
 * @param {{getItem: (key: string) => string|null, setItem: (key: string, value: string) => void, removeItem: (key: string) => void}} storage Session storage adapter.
 * @param {(message: string) => void} onError Reports unavailable or invalid session storage to the operator.
 * @returns {EndpointGroupSessionStore} Session-only read/write operations.
 */
export function createEndpointGroupSessionStore(storage, onError) {
  return {
    read() {
      try {
        const stored = storage.getItem(ENDPOINT_GROUP_SESSION_KEY);
        if (stored === null) return [];
        const groups = JSON.parse(stored);
        if (!Array.isArray(groups) || !groups.every((group) => typeof group === "string")) {
          throw new TypeError("Invalid endpoint group session data.");
        }
        return groups;
      } catch {
        onError("Unable to restore endpoint groups for this session. Groups start expanded.");
        return [];
      }
    },
    write(groups) {
      try {
        if (groups === null) storage.removeItem(ENDPOINT_GROUP_SESSION_KEY);
        else storage.setItem(ENDPOINT_GROUP_SESSION_KEY, JSON.stringify(groups));
      } catch {
        onError("Unable to save endpoint groups for this session. Changes remain active on this page.");
      }
    },
  };
}

/**
 * Creates a preference store backed by a Web Storage-compatible object.
 * Invalid or unavailable storage falls back to normalized in-memory preferences.
 *
 * @param {{getItem: (key: string) => string|null, setItem: (key: string, value: string) => void}} storage Storage adapter.
 * @returns {DashboardPreferencesStore} Preference store.
 */
export function createDashboardPreferencesStore(storage) {
  let preferences;
  try {
    preferences = normalizePreferences(
      JSON.parse(storage.getItem(DASHBOARD_PREFERENCES_KEY)),
      storage.getItem(LEGACY_THEME_KEY)
    );
  } catch {
    preferences = normalizePreferences(null);
  }

  return Object.freeze({
    get() {
      return preferences;
    },
    update(changes) {
      preferences = normalizePreferences({ ...preferences, ...changes });
      try {
        storage.setItem(DASHBOARD_PREFERENCES_KEY, JSON.stringify({ version: VERSION, ...preferences }));
      } catch {
        // Preferences remain active for this page when storage is unavailable.
      }
      return preferences;
    },
  });
}
