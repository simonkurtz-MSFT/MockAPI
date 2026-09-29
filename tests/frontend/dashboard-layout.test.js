import { describe, expect, it, vi } from "vitest";
import { createDashboardLayoutController, resolveDashboardLayout } from "../../src/MockAPI/wwwroot/dashboard-layout.js";

const measurements = {
  viewportWidth: 1920,
  contentWidth: 1849,
  columnGap: 20,
  commandWidths: [250, 380, 160],
  commandGap: 16,
};

describe("measured dashboard layout policy", () => {
  it.each([
    ["stacked", 3000, "stacked"],
    ["columns", 1099, "stacked"],
    ["columns", 1100, "columns"],
    ["auto", 1399, "stacked"],
    ["auto", 1400, "columns"],
  ])("resolves %s at the %i pixel eligibility boundary", (preference, viewportWidth, expected) => {
    expect(
      resolveDashboardLayout(preference, {
        ...measurements,
        viewportWidth,
        contentWidth: viewportWidth - 56,
        commandWidths: [100, 150, 100],
      })
    ).toBe(expected);
  });

  it.each(["auto", "columns"])("uses exact command widths and both kinds of gaps for %s", (preference) => {
    const metrics = { ...measurements, contentWidth: 1664 };
    // Commands need 250 + 380 + 160 + 2*16 = 822px; (1664 - 20)/2 fits exactly.
    expect(resolveDashboardLayout(preference, metrics)).toBe("columns");
    expect(resolveDashboardLayout(preference, { ...metrics, contentWidth: 1663.5 })).toBe("stacked");
    expect(resolveDashboardLayout(preference, { ...metrics, commandGap: 16.25 })).toBe("stacked");
    expect(resolveDashboardLayout(preference, { ...metrics, columnGap: 20.5 })).toBe("stacked");
    expect(resolveDashboardLayout(preference, { ...metrics, commandWidths: [251, 380, 160] })).toBe("stacked");
  });

  it("does not add a gap for a single command group", () => {
    expect(
      resolveDashboardLayout("columns", {
        ...measurements,
        contentWidth: 520,
        commandWidths: [250],
        commandGap: 100,
      })
    ).toBe("columns");
  });
});

function createLayoutFixture() {
  const main = { paddingLeft: "28px", paddingRight: "28px" };
  const layout = { columnGap: "20px" };
  const actions = { columnGap: "16px" };
  const groups = measurements.commandWidths.map((width) => ({
    width,
    getBoundingClientRect() {
      return { width: this.width };
    },
  }));
  const nodes = { main, ".dashboard-layout": layout, ".command-actions": actions };
  const documentRoot = {
    documentElement: {
      clientWidth: 1905,
      dataset: { statisticsCollapsed: "true", requestLogCollapsed: "false" },
    },
    querySelector: (selector) => nodes[selector] ?? null,
    querySelectorAll: () => groups,
  };
  const windowRoot = Object.assign(new EventTarget(), {
    innerWidth: 1920,
    getComputedStyle: vi.fn((element) => element),
  });
  let preference = "auto";
  const preferencesStore = { get: () => ({ dashboardLayout: preference }), update: vi.fn() };
  const observer = { observe: vi.fn(), disconnect: vi.fn() };
  const createResizeObserver = vi.fn(() => observer);
  return {
    documentRoot,
    windowRoot,
    preferencesStore,
    observer,
    createResizeObserver,
    groups,
    nodes,
    setPreference: (value) => {
      preference = value;
    },
    options: { documentRoot, windowRoot, preferencesStore, createResizeObserver },
  };
}

describe("dashboard layout lifecycle", () => {
  it("observes intrinsic widths, reacts to resize/settings, and never overwrites saved choices", () => {
    const fixture = createLayoutFixture();
    const controller = createDashboardLayoutController(fixture.options);
    const root = fixture.documentRoot.documentElement;
    expect(root.dataset.dashboardLayout).toBe("columns");
    expect(fixture.observer.observe.mock.calls.map(([element]) => element)).toEqual(fixture.groups);

    fixture.groups[0].width = 500;
    fixture.createResizeObserver.mock.calls[0][0]();
    expect(root.dataset.dashboardLayout).toBe("stacked");
    fixture.groups[0].width = 250;
    fixture.createResizeObserver.mock.calls[0][0]();
    expect(root.dataset.dashboardLayout).toBe("columns");

    fixture.windowRoot.innerWidth = 1200;
    root.clientWidth = 1185;
    fixture.windowRoot.dispatchEvent(new Event("resize"));
    expect(root.dataset.dashboardLayout).toBe("stacked");
    fixture.windowRoot.innerWidth = 1920;
    root.clientWidth = 1905;
    fixture.windowRoot.dispatchEvent(new Event("resize"));
    expect(root.dataset.dashboardLayout).toBe("columns");
    fixture.setPreference("stacked");
    controller.refresh();
    expect(root.dataset.dashboardLayout).toBe("stacked");
    expect(root.dataset.statisticsCollapsed).toBe("true");
    expect(root.dataset.requestLogCollapsed).toBe("false");
    expect(fixture.preferencesStore.update).not.toHaveBeenCalled();
    controller.dispose();
  });

  it("subtracts padding and scrollbar space rather than using capped main or viewport widths", () => {
    const fixture = createLayoutFixture();
    fixture.documentRoot.documentElement.clientWidth = 1720;
    const controller = createDashboardLayoutController(fixture.options);
    expect(fixture.documentRoot.documentElement.dataset.dashboardLayout).toBe("columns");
    fixture.documentRoot.documentElement.clientWidth = 1719;
    controller.refresh();
    expect(fixture.documentRoot.documentElement.dataset.dashboardLayout).toBe("stacked");
    controller.dispose();
  });

  it("releases listeners and ignores already queued observation after disposal", () => {
    const fixture = createLayoutFixture();
    const controller = createDashboardLayoutController(fixture.options);
    const removeListener = vi.spyOn(fixture.windowRoot, "removeEventListener");
    controller.dispose();
    expect(removeListener).toHaveBeenCalledWith("resize", controller.refresh);
    expect(fixture.observer.disconnect).toHaveBeenCalledOnce();
    fixture.windowRoot.getComputedStyle.mockClear();
    controller.dispose();
    controller.refresh();
    fixture.createResizeObserver.mock.calls[0][0]();
    fixture.windowRoot.dispatchEvent(new Event("resize"));
    expect(fixture.windowRoot.getComputedStyle).not.toHaveBeenCalled();
  });

  it.each(["main", ".dashboard-layout", ".command-actions", ".command-group"])(
    "reports missing %s markup before allocating listeners",
    (selector) => {
      const fixture = createLayoutFixture();
      if (selector === ".command-group") fixture.groups.length = 0;
      else delete fixture.nodes[selector];
      expect(() => createDashboardLayoutController(fixture.options)).toThrow("Missing dashboard layout markup");
      expect(fixture.createResizeObserver).not.toHaveBeenCalled();
    }
  );
});
