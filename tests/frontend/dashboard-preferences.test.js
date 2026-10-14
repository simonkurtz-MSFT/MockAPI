import { describe, expect, it, vi } from "vitest";
import {
  createDashboardPreferencesStore,
  DASHBOARD_PREFERENCES_KEY,
} from "../../src/MockAPI/wwwroot/dashboard-preferences.js";

const defaults = {
  theme: null,
  dashboardLayout: "auto",
  endpointTestDialogAlignment: "right",
  endpointsCollapsed: false,
  kjUiStyle: false,
  requestLogCollapsed: false,
  requestLogTimeZone: "utc",
  responseBodyPresentation: "raw",
  statisticsView: "graph",
  statisticsCollapsed: false,
  tutorialDismissed: false,
};

function createStorage(values = {}) {
  const entries = new Map(Object.entries(values));
  return {
    entries,
    getItem: vi.fn((key) => entries.get(key) ?? null),
    setItem: vi.fn((key, value) => entries.set(key, value)),
  };
}

describe("createDashboardPreferencesStore", () => {
  it("returns immutable defaults when no preferences exist", () => {
    const preferences = createDashboardPreferencesStore(createStorage()).get();

    expect(preferences).toEqual(defaults);
    expect(Object.isFrozen(preferences)).toBe(true);
  });

  it("loads supported persisted preferences", () => {
    const storage = createStorage({
      [DASHBOARD_PREFERENCES_KEY]: JSON.stringify({
        version: 1,
        theme: "dark",
        dashboardLayout: "columns",
        endpointTestDialogAlignment: "left",
        endpointPageSize: 50,
        endpointsCollapsed: true,
        kjUiStyle: true,
        requestLogCollapsed: true,
        requestLogTimeZone: "local",
        responseBodyPresentation: "pretty",
        statisticsView: "table",
        statisticsCollapsed: true,
        tutorialDismissed: true,
      }),
    });

    expect(createDashboardPreferencesStore(storage).get()).toEqual({
      theme: "dark",
      dashboardLayout: "columns",
      endpointTestDialogAlignment: "left",
      endpointsCollapsed: true,
      kjUiStyle: true,
      requestLogCollapsed: true,
      requestLogTimeZone: "local",
      responseBodyPresentation: "pretty",
      statisticsView: "table",
      statisticsCollapsed: true,
      tutorialDismissed: true,
    });
  });

  it("expands panels by default when other preferences exist but no collapse choices were saved", () => {
    const storage = createStorage({
      [DASHBOARD_PREFERENCES_KEY]: JSON.stringify({
        version: 1,
        dashboardLayout: "columns",
        theme: "dark",
        tutorialDismissed: true,
      }),
    });

    expect(createDashboardPreferencesStore(storage).get()).toEqual({
      ...defaults,
      dashboardLayout: "columns",
      theme: "dark",
      tutorialDismissed: true,
    });
    expect(storage.setItem).not.toHaveBeenCalled();
  });

  it("accepts unversioned records and a legacy theme fallback", () => {
    const storage = createStorage({
      [DASHBOARD_PREFERENCES_KEY]: JSON.stringify({ theme: "invalid" }),
      "mockapi.theme": "light",
    });

    expect(createDashboardPreferencesStore(storage).get().theme).toBe("light");
  });

  it("rejects unsupported records and invalid field values", () => {
    const storage = createStorage({
      [DASHBOARD_PREFERENCES_KEY]: JSON.stringify({
        version: 2,
        theme: "dark",
        dashboardLayout: "columns",
        endpointTestDialogAlignment: "top",
        endpointPageSize: 50,
        endpointsCollapsed: "true",
        kjUiStyle: "true",
        requestLogCollapsed: "true",
        responseBodyPresentation: "formatted",
        statisticsView: "cards",
        statisticsCollapsed: "false",
        tutorialDismissed: "true",
      }),
      "mockapi.theme": "invalid",
    });

    expect(createDashboardPreferencesStore(storage).get()).toEqual(defaults);
  });

  it("falls back to defaults when reading storage fails", () => {
    const storage = createStorage();
    storage.getItem.mockImplementation(() => {
      throw new Error("blocked");
    });

    expect(createDashboardPreferencesStore(storage).get()).toEqual(defaults);
  });

  it("merges, validates, and persists updates as a versioned record", () => {
    const storage = createStorage();
    const store = createDashboardPreferencesStore(storage);

    const preferences = store.update({
      theme: "dark",
      dashboardLayout: "stacked",
      endpointTestDialogAlignment: "center",
      kjUiStyle: true,
      statisticsView: "table",
      tutorialDismissed: true,
      requestLogTimeZone: "local",
      responseBodyPresentation: "pretty",
    });

    expect(preferences).toEqual({
      ...defaults,
      theme: "dark",
      dashboardLayout: "stacked",
      endpointTestDialogAlignment: "center",
      kjUiStyle: true,
      statisticsView: "table",
      tutorialDismissed: true,
      requestLogTimeZone: "local",
      responseBodyPresentation: "pretty",
    });
    expect(store.get()).toBe(preferences);
    expect(JSON.parse(storage.entries.get(DASHBOARD_PREFERENCES_KEY))).toEqual({ version: 1, ...preferences });

    expect(
      store.update({
        theme: "system",
        dashboardLayout: "sideways",
        endpointTestDialogAlignment: "bottom",
        kjUiStyle: "true",
        statisticsView: "cards",
        requestLogTimeZone: "invalid",
        responseBodyPresentation: "invalid",
      })
    ).toEqual({
      ...defaults,
      tutorialDismissed: true,
    });
  });

  it("keeps updates active when writing storage fails", () => {
    const storage = createStorage();
    storage.setItem.mockImplementation(() => {
      throw new Error("quota exceeded");
    });
    const store = createDashboardPreferencesStore(storage);

    expect(
      store.update({
        endpointsCollapsed: true,
        requestLogCollapsed: true,
        statisticsCollapsed: true,
        requestLogTimeZone: "local",
      })
    ).toEqual({
      ...defaults,
      endpointsCollapsed: true,
      requestLogCollapsed: true,
      statisticsCollapsed: true,
      requestLogTimeZone: "local",
    });
  });

  it.each([undefined, null, "invalid", "UTC", true])(
    "defaults an absent or invalid time zone %s to UTC",
    (requestLogTimeZone) => {
      const storage = createStorage({
        [DASHBOARD_PREFERENCES_KEY]: JSON.stringify({ version: 1, requestLogTimeZone }),
      });
      expect(createDashboardPreferencesStore(storage).get().requestLogTimeZone).toBe("utc");
    }
  );

  it.each([undefined, null, "invalid", "PRETTY", true])(
    "defaults an absent or invalid response presentation %s to raw",
    (responseBodyPresentation) => {
      const storage = createStorage({
        [DASHBOARD_PREFERENCES_KEY]: JSON.stringify({ version: 1, responseBodyPresentation }),
      });
      expect(createDashboardPreferencesStore(storage).get().responseBodyPresentation).toBe("raw");
    }
  );
});
