import { describe, expect, it, vi } from "vitest";
import {
  createDashboardPreferencesStore,
  DASHBOARD_PREFERENCES_KEY,
} from "../../src/MockAPI/wwwroot/dashboard-preferences.js";

const defaults = {
  theme: null,
  dashboardLayout: "auto",
  endpointTestDialogAlignment: "right",
  endpointPageSize: 10,
  endpointsCollapsed: false,
  requestLogCollapsed: false,
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
        requestLogCollapsed: true,
        statisticsView: "table",
        statisticsCollapsed: true,
        tutorialDismissed: true,
      }),
    });

    expect(createDashboardPreferencesStore(storage).get()).toEqual({
      theme: "dark",
      dashboardLayout: "columns",
      endpointTestDialogAlignment: "left",
      endpointPageSize: 50,
      endpointsCollapsed: true,
      requestLogCollapsed: true,
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
        requestLogCollapsed: "true",
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
      endpointPageSize: 100,
      statisticsView: "table",
      tutorialDismissed: true,
    });

    expect(preferences).toEqual({
      ...defaults,
      theme: "dark",
      dashboardLayout: "stacked",
      endpointTestDialogAlignment: "center",
      endpointPageSize: 100,
      statisticsView: "table",
      tutorialDismissed: true,
    });
    expect(store.get()).toBe(preferences);
    expect(JSON.parse(storage.entries.get(DASHBOARD_PREFERENCES_KEY))).toEqual({ version: 1, ...preferences });

    expect(
      store.update({
        theme: "system",
        dashboardLayout: "sideways",
        endpointTestDialogAlignment: "bottom",
        endpointPageSize: 75,
        statisticsView: "cards",
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

    expect(store.update({ endpointsCollapsed: true, requestLogCollapsed: true, statisticsCollapsed: true })).toEqual({
      ...defaults,
      endpointsCollapsed: true,
      requestLogCollapsed: true,
      statisticsCollapsed: true,
    });
  });
});
