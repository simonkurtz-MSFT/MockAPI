import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { describe, expect, it, vi } from "vitest";
import { createDashboardPreferencesStore } from "../../src/MockAPI/wwwroot/dashboard-preferences.js";

// Exercise the actual inline bootstrap, not a copied implementation of first-paint policy.
const html = readFileSync(new URL("../../src/MockAPI/wwwroot/index.html", import.meta.url), "utf8");
const bootstrap = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function boot({ value = null, legacyTheme = null, blocked = false, width = 1920, dark = false, search = "" } = {}) {
  const storage = {
    getItem: (key) => {
      if (blocked) throw new Error("Storage unavailable");
      return key === "mockapi.preferences" ? value : legacyTheme;
    },
    setItem: vi.fn(),
  };
  const root = {
    dataset: {},
    setAttribute(name, value) {
      this[name] = value;
    },
  };
  runInNewContext(bootstrap, {
    URLSearchParams,
    document: { documentElement: root },
    window: {
      localStorage: storage,
      location: { search },
      matchMedia: (query) => ({
        matches: query.includes("min-width") ? width >= Number(query.match(/\d+/)[0]) : dark,
      }),
    },
  });
  return { root, storage, preferences: createDashboardPreferencesStore(storage).get() };
}

describe("first-paint and runtime preference agreement", () => {
  it.each([
    null,
    "{bad json",
    "null",
    "false",
    "42",
    '"text"',
    "[]",
    JSON.stringify({ version: 1 }),
    JSON.stringify({ statisticsCollapsed: true, endpointsCollapsed: false, requestLogCollapsed: true, theme: "dark" }),
    JSON.stringify({
      version: 1,
      statisticsCollapsed: true,
      endpointsCollapsed: true,
      requestLogCollapsed: true,
      theme: "light",
    }),
    JSON.stringify({
      version: 2,
      statisticsCollapsed: true,
      endpointsCollapsed: true,
      requestLogCollapsed: true,
      theme: "dark",
    }),
    JSON.stringify({
      version: 1,
      statisticsCollapsed: "true",
      endpointsCollapsed: 1,
      requestLogCollapsed: {},
      theme: "invalid",
    }),
  ])("keeps theme and collapse state consistent for stored %s", (value) => {
    const { root, storage, preferences } = boot({ value, legacyTheme: "light" });
    for (const key of ["statisticsCollapsed", "endpointsCollapsed", "requestLogCollapsed"]) {
      expect(root.dataset[key]).toBe(String(preferences[key]));
    }
    expect(root["data-theme"]).toBe(preferences.theme ?? "light");
    expect(storage.setItem).not.toHaveBeenCalled();
  });

  it("uses defaults and system theme when storage is unavailable", () => {
    const { root, preferences } = boot({ blocked: true, dark: true });
    expect(root["data-theme"]).toBe("dark");
    expect(root.dataset.dashboardLayout).toBe("stacked");
    expect(preferences.theme).toBeNull();
    for (const key of ["statisticsCollapsed", "endpointsCollapsed", "requestLogCollapsed"]) {
      expect(root.dataset[key]).toBe("false");
      expect(preferences[key]).toBe(false);
    }
  });

  it("keeps the preview theme override out of persisted preferences", () => {
    const { root, preferences, storage } = boot({ legacyTheme: "light", search: "?scoutTheme=dark" });
    expect(root["data-theme"]).toBe("dark");
    expect(preferences.theme).toBe("light");
    expect(storage.setItem).not.toHaveBeenCalled();
  });

  it.each([
    ["auto", 1759, "stacked"],
    ["auto", 1760, "columns"],
    ["columns", 1759, "stacked"],
    ["columns", 1760, "columns"],
    ["stacked", 1920, "stacked"],
    ["invalid", 1920, "columns"],
  ])("starts %s conservatively at %i pixels until command measurements exist", (dashboardLayout, width, expected) => {
    const { root, storage } = boot({ width, value: JSON.stringify({ version: 1, dashboardLayout }) });
    expect(root.dataset.dashboardLayout).toBe(expected);
    expect(storage.setItem).not.toHaveBeenCalled();
  });
});
