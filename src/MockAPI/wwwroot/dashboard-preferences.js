"use strict";

/** Storage key for the versioned dashboard preference document. @type {string} */
export const DASHBOARD_PREFERENCES_KEY = "mockapi.preferences";
const LEGACY_THEME_KEY = "mockapi.theme";
const VERSION = 1;

/** @typedef {"auto"|"columns"|"stacked"} DashboardLayout */
/** @typedef {"left"|"center"|"right"} TestBladeAlignment */
/** @typedef {"graph"|"table"} StatisticsView */
/** @typedef {10|25|50|100} EndpointPageSize */

/**
 * @typedef {Object} DashboardPreferences
 * @property {"dark"|"light"|null} theme Explicit theme, or `null` to follow the operating system.
 * @property {DashboardLayout} dashboardLayout Preferred dashboard workspace layout.
 * @property {TestBladeAlignment} endpointTestDialogAlignment Preferred endpoint test dialog alignment.
 * @property {EndpointPageSize} endpointPageSize Endpoint rows per page.
 * @property {boolean} endpointsCollapsed Whether the endpoint section is collapsed.
 * @property {boolean} requestLogCollapsed Whether the request log section is collapsed.
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
    endpointPageSize: [10, 25, 50, 100].includes(supported.endpointPageSize) ? supported.endpointPageSize : 10,
    endpointsCollapsed: typeof supported.endpointsCollapsed === "boolean" ? supported.endpointsCollapsed : false,
    requestLogCollapsed: typeof supported.requestLogCollapsed === "boolean" ? supported.requestLogCollapsed : false,
    statisticsView: ["graph", "table"].includes(supported.statisticsView) ? supported.statisticsView : "graph",
    statisticsCollapsed: typeof supported.statisticsCollapsed === "boolean" ? supported.statisticsCollapsed : false,
    tutorialDismissed: supported.tutorialDismissed === true,
  });
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
