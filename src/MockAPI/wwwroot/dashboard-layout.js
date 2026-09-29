"use strict";

/**
 * @typedef {object} DashboardLayoutMeasurements
 * @property {number} viewportWidth CSS viewport width, including its scrollbar.
 * @property {number} contentWidth Available page width after subtracting scrollbars and main padding.
 * @property {number} columnGap Gap between dashboard columns.
 * @property {readonly number[]} commandWidths Measured command-group widths.
 * @property {number} commandGap Gap between command groups.
 */

/**
 * Resolves a saved preference without changing it. Columns must fit every command group on one row.
 * The inline first-paint bootstrap is deliberately conservative until these measurements are available.
 * @param {import("./dashboard-preferences.js").DashboardLayout} preference Saved layout preference.
 * @param {DashboardLayoutMeasurements} measurements Current viewport and intrinsic command widths.
 * @returns {"columns"|"stacked"} Effective layout; equality at the fit boundary permits columns.
 */
export function resolveDashboardLayout(preference, measurements) {
  if (preference === "stacked" || measurements.viewportWidth < 1100) return "stacked";
  if (preference === "auto" && measurements.viewportWidth < 1400) return "stacked";
  const gaps = Math.max(0, measurements.commandWidths.length - 1) * measurements.commandGap;
  const commandsWidth = measurements.commandWidths.reduce((sum, width) => sum + width, 0) + gaps;
  const columnWidth = (measurements.contentWidth - measurements.columnGap) / 2;
  return commandsWidth <= columnWidth ? "columns" : "stacked";
}

/**
 * @typedef {object} DashboardLayoutController
 * @property {() => void} refresh Re-measures and applies the current preference; never writes preferences or collapse state.
 * @property {() => void} dispose Releases resize observation and the window listener; later refreshes are inert.
 */

/**
 * Owns measured layout and resize observation for the page lifetime.
 * @param {object} options Explicit browser and preference dependencies.
 * @param {Document} options.documentRoot Dashboard markup.
 * @param {Window} options.windowRoot Viewport, styles, and resize events.
 * @param {Pick<import("./dashboard-preferences.js").DashboardPreferencesStore, "get">} options.preferencesStore Read-only preference access.
 * @param {(callback: () => void) => Pick<ResizeObserver, "observe"|"disconnect">} options.createResizeObserver Observer factory.
 * @returns {DashboardLayoutController} Initialized layout owner.
 * @throws {Error} Required layout markup is missing.
 */
export function createDashboardLayoutController({ documentRoot, windowRoot, preferencesStore, createResizeObserver }) {
  const main = documentRoot.querySelector("main");
  const layout = documentRoot.querySelector(".dashboard-layout");
  const actions = documentRoot.querySelector(".command-actions");
  const groups = [...documentRoot.querySelectorAll(".command-group")];
  if (!main || !layout || !actions || groups.length === 0) {
    throw new Error("Missing dashboard layout markup.");
  }
  let disposed = false;

  function refresh() {
    if (disposed) return;
    const mainStyle = windowRoot.getComputedStyle(main);
    // Stacked main has a max-width; use the available viewport for the proposed uncapped columns.
    const contentWidth =
      documentRoot.documentElement.clientWidth - parseFloat(mainStyle.paddingLeft) - parseFloat(mainStyle.paddingRight);
    documentRoot.documentElement.dataset.dashboardLayout = resolveDashboardLayout(
      preferencesStore.get().dashboardLayout,
      {
        viewportWidth: windowRoot.innerWidth,
        contentWidth,
        columnGap: parseFloat(windowRoot.getComputedStyle(layout).columnGap),
        commandWidths: groups.map((group) => group.getBoundingClientRect().width),
        commandGap: parseFloat(windowRoot.getComputedStyle(actions).columnGap),
      }
    );
  }

  refresh();
  const observer = createResizeObserver(refresh);
  for (const group of groups) observer.observe(group);
  windowRoot.addEventListener("resize", refresh);
  return {
    refresh,
    dispose() {
      disposed = true;
      observer.disconnect();
      windowRoot.removeEventListener("resize", refresh);
    },
  };
}
