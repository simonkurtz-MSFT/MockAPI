import {
  expect,
  test,
  emptyDocument,
  importDocument,
  expectNoUnreviewedAccessibilityViolations,
} from "./dashboard-fixtures.js";

test("uses separate endpoint and activity columns only on wide screens", async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");

  const commands = page.locator(".command-bar");
  const endpoints = page.locator(".workspace");
  const statistics = page.locator(".statistics-workspace");
  const requestLog = page.locator(".request-log-workspace");
  const wideBounds = {
    commands: await commands.boundingBox(),
    endpoints: await endpoints.boundingBox(),
    statistics: await statistics.boundingBox(),
    requestLog: await requestLog.boundingBox(),
  };

  expect(wideBounds.commands.x).toBe(wideBounds.endpoints.x);
  expect(wideBounds.commands.y).toBe(wideBounds.statistics.y);
  expect(wideBounds.commands.y + wideBounds.commands.height).toBeLessThanOrEqual(wideBounds.endpoints.y);
  expect(wideBounds.endpoints.x).toBeLessThan(wideBounds.statistics.x);
  expect(wideBounds.statistics.x).toBe(wideBounds.requestLog.x);
  expect(Math.abs(wideBounds.endpoints.width - wideBounds.statistics.width)).toBeLessThanOrEqual(1);
  const contentWidth = await page.evaluate(() => document.documentElement.clientWidth);
  expect(wideBounds.endpoints.x).toBe(28);
  expect(contentWidth - (wideBounds.statistics.x + wideBounds.statistics.width)).toBe(28);

  await page.setViewportSize({ width: 1200, height: 1000 });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  const stackedBounds = {
    endpoints: await endpoints.boundingBox(),
    statistics: await statistics.boundingBox(),
    requestLog: await requestLog.boundingBox(),
  };

  expect(stackedBounds.statistics.y + stackedBounds.statistics.height).toBeLessThanOrEqual(stackedBounds.requestLog.y);
  expect(stackedBounds.requestLog.y + stackedBounds.requestLog.height).toBeLessThanOrEqual(stackedBounds.endpoints.y);
});

test("places the visible command groups at the left above Endpoints and flush with Statistics", async ({ page }) => {
  for (const width of [1760, 1920, 3000]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");

    const endpoints = await page.locator(".workspace").boundingBox();
    const statistics = await page.locator(".statistics-workspace").boundingBox();
    const groups = await page.locator(".command-group").all();
    const bounds = await Promise.all(groups.map((group) => group.boundingBox()));

    expect(bounds[0].x).toBe(endpoints.x);
    expect(bounds[0].y).toBe(statistics.y);
    for (const group of bounds) {
      expect(group.x).toBeGreaterThanOrEqual(endpoints.x);
      expect(group.x + group.width).toBeLessThanOrEqual(endpoints.x + endpoints.width);
      expect(group.y + group.height).toBeLessThan(endpoints.y);
      expect(group.y).toBe(statistics.y);
    }
    const commandsBottom = Math.max(...bounds.map((group) => group.y + group.height));
    expect(endpoints.y - commandsBottom).toBe(20);
  }
});

for (const scenario of [
  {
    name: "saved collapsed panels",
    preferences: { version: 1, statisticsCollapsed: true, requestLogCollapsed: true, endpointsCollapsed: true },
    collapsed: ["endpoint", "statistics", "request-log"],
  },
  {
    name: "saved collapsed activity panels",
    preferences: { version: 1, statisticsCollapsed: true, requestLogCollapsed: true, endpointsCollapsed: false },
    collapsed: ["statistics", "request-log"],
  },
  { name: "missing collapse preferences", preferences: { version: 1 }, collapsed: [] },
  {
    name: "unsupported preferences",
    preferences: { version: 2, statisticsCollapsed: true, requestLogCollapsed: true, endpointsCollapsed: true },
    collapsed: [],
  },
  {
    name: "invalid collapse preferences",
    preferences: { version: 1, statisticsCollapsed: "true", requestLogCollapsed: "true", endpointsCollapsed: "true" },
    collapsed: [],
  },
]) {
  test(`renders ${scenario.name} consistently before dashboard modules load`, async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1000 });
    await page.evaluate((preferences) => {
      localStorage.setItem("mockapi.preferences", JSON.stringify({ ...preferences, tutorialDismissed: true }));
    }, scenario.preferences);
    let releaseModule;
    const moduleReady = new Promise((resolve) => {
      releaseModule = resolve;
    });
    await page.route(/\/app\.js\?/, async (route) => {
      await moduleReady;
      await route.continue();
    });
    try {
      await page.reload({ waitUntil: "commit" });
      await expect(page.locator("#connection-status")).toHaveText("Connecting");
      await expect
        .poll(() => page.locator('link[rel="stylesheet"]').evaluate((link) => Boolean(link.sheet)))
        .toBe(true);
      for (const id of ["endpoint", "statistics", "request-log"]) {
        await expect(page.locator(`#${id}-body`)).toHaveCount(1);
        if (scenario.collapsed.includes(id)) await expect(page.locator(`#${id}-body`)).toBeHidden();
        else await expect(page.locator(`#${id}-body`)).toBeVisible();
      }
      const selectors = [".workspace", ".statistics-workspace", ".request-log-workspace"];
      const before = await Promise.all(selectors.map((selector) => page.locator(selector).boundingBox()));
      if (scenario.collapsed.length > 0) {
        for (const [index, id] of ["endpoint", "statistics", "request-log"].entries()) {
          if (scenario.collapsed.includes(id)) expect(before[index].height).toBeLessThan(180);
        }
        await expect(page.locator("#statistics-controls")).toBeHidden();
        if (scenario.collapsed.includes("endpoint")) await expect(page.locator("#endpoint-controls")).toBeHidden();
        else await expect(page.locator("#endpoint-controls")).toBeVisible();
      }

      releaseModule();
      await expect(page.locator("#connection-status")).toHaveText("Live");
      for (const id of ["endpoint", "statistics", "request-log"]) {
        const collapsed = scenario.collapsed.includes(id);
        await expect(page.locator(`#${id}-toggle`)).toHaveAttribute("aria-expanded", String(!collapsed));
        if (collapsed) await expect(page.locator(`#${id}-body`)).toBeHidden();
        else await expect(page.locator(`#${id}-body`)).toBeVisible();
      }
      if (scenario.collapsed.length > 0) {
        const after = await Promise.all(selectors.map((selector) => page.locator(selector).boundingBox()));
        for (let index = 0; index < before.length; index += 1) {
          expect(Math.abs(after[index].height - before[index].height)).toBeLessThanOrEqual(1);
          expect(Math.abs(after[index].y - before[index].y)).toBeLessThanOrEqual(1);
        }
        for (const id of scenario.collapsed) {
          await page.locator(`#${id}-toggle`).click();
          await expect(page.locator(`#${id}-body`)).toBeVisible();
        }
      }
    } finally {
      releaseModule();
    }
  });
}

test("defaults two-column panels to expanded and aligned without overriding saved collapse choices", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await page.evaluate(() => {
    localStorage.setItem(
      "mockapi.preferences",
      JSON.stringify({ version: 1, dashboardLayout: "columns", tutorialDismissed: true })
    );
  });
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");

  for (const id of ["endpoint", "statistics", "request-log"]) {
    await expect(page.locator(`#${id}-toggle`)).toHaveAttribute("aria-expanded", "true");
    await expect(page.locator(`#${id}-body`)).toBeVisible();
  }

  const endpoints = await page.locator(".workspace").boundingBox();
  const statistics = await page.locator(".statistics-workspace").boundingBox();
  const log = await page.locator(".request-log-workspace").boundingBox();
  expect(endpoints.height).toBeGreaterThanOrEqual(700);
  expect(log.height).toBeGreaterThanOrEqual(400);
  expect(statistics.y + statistics.height).toBeLessThan(log.y);
  expect(Math.abs(endpoints.y + endpoints.height - log.y - log.height)).toBeLessThanOrEqual(1);

  for (const id of ["endpoint", "statistics", "request-log"]) {
    await page.locator(`#${id}-toggle`).click();
    await expect(page.locator(`#${id}-body`)).toBeHidden();
  }
  await page.reload();
  for (const viewport of [
    { width: 1920, height: 1000 },
    { width: 900, height: 1000 },
    { width: 1920, height: 1000 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(page.locator("html")).toHaveAttribute(
      "data-dashboard-layout",
      viewport.width === 900 ? "stacked" : "columns"
    );
    for (const id of ["endpoint", "statistics", "request-log"]) {
      await expect(page.locator(`#${id}-toggle`)).toHaveAttribute("aria-expanded", "false");
      await expect(page.locator(`#${id}-body`)).toBeHidden();
    }
  }

  for (const id of ["endpoint", "statistics", "request-log"]) {
    await page.locator(`#${id}-toggle`).click();
  }
  const expandedEndpoints = await page.locator(".workspace").boundingBox();
  const expandedLog = await page.locator(".request-log-workspace").boundingBox();
  expect(
    Math.abs(expandedEndpoints.y + expandedEndpoints.height - expandedLog.y - expandedLog.height)
  ).toBeLessThanOrEqual(1);
});

test("aligns expanded column bottoms without horizontal scrolling on an empty dashboard", async ({ page, request }) => {
  await request.post("/__mockapi/api/statistics/reset");
  await expect(page.locator("#request-log-count")).toHaveText("0");
  for (const viewport of [
    { width: 1760, height: 900 },
    { width: 1920, height: 1080 },
    { width: 1920, height: 700 },
    { width: 2200, height: 1300 },
    { width: 2560, height: 1440 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");
    const endpoints = await page.locator(".workspace").boundingBox();
    const log = await page.locator(".request-log-workspace").boundingBox();
    expect(Math.abs(endpoints.y + endpoints.height - log.y - log.height)).toBeLessThanOrEqual(1);
    for (const selector of [".table-wrap", ".request-log-table", ".statistics-chart-scroll"]) {
      const dimensions = await page.locator(selector).evaluate((element) => ({
        width: element.clientWidth,
        contentWidth: element.scrollWidth,
        overflowX: getComputedStyle(element).overflowX,
      }));
      expect(dimensions.contentWidth).toBeLessThanOrEqual(dimensions.width);
      expect(dimensions.overflowX).toBe("auto");
    }
    await page.getByRole("button", { name: "Collapse statistics" }).click();
    const collapsedStatisticsEndpoints = await page.locator(".workspace").boundingBox();
    const expandedLog = await page.locator(".request-log-workspace").boundingBox();
    expect(
      Math.abs(
        collapsedStatisticsEndpoints.y + collapsedStatisticsEndpoints.height - expandedLog.y - expandedLog.height
      )
    ).toBeLessThanOrEqual(1);
    await page.getByRole("button", { name: "Expand statistics" }).click();
  }
});

test("fits populated endpoint rows within the wide-screen endpoint column", async ({ page }) => {
  await page.setViewportSize({ width: 1760, height: 900 });
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator(".endpoint-row")).toHaveCount(7);
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");

  const dimensions = await page.locator(".table-wrap").evaluate((element) => ({
    width: element.clientWidth,
    contentWidth: element.scrollWidth,
  }));
  expect(dimensions.contentWidth).toBeLessThanOrEqual(dimensions.width);
});

test("falls back to stacked before commands wrap and keeps endpoint filters compact and aligned", async ({ page }) => {
  const observedLayouts = new Set();

  for (const width of [320, 600, 1100, 1400, 1600, 1710, 1760, 1920, 3000, 1600]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const layout = await page.locator("html").getAttribute("data-dashboard-layout");
    expect(["stacked", "columns"]).toContain(layout);
    observedLayouts.add(layout);
    const search = await page.locator("#filter-text").boundingBox();
    const toggle = await page.locator("#endpoint-toggle").boundingBox();
    expect(search.width).toBeLessThanOrEqual(220);
    expect(Math.abs(toggle.y - search.y)).toBeLessThanOrEqual(2);
    const commandRows = await page
      .locator(".command-group")
      .evaluateAll((groups) => new Set(groups.map((group) => group.getBoundingClientRect().y)).size);
    if (layout === "columns") expect(commandRows).toBe(1);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)
    ).toBe(true);
  }
  expect(observedLayouts).toEqual(new Set(["stacked", "columns"]));

  await page.setViewportSize({ width: 1920, height: 1000 });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");
  await page.evaluate(() => (document.documentElement.style.fontSize = "24px"));
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  await page.evaluate(() => document.documentElement.style.removeProperty("font-size"));
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");
  await page.getByRole("searchbox", { name: "Filter endpoints", exact: true }).fill("not an endpoint");
  await expect(page.locator("#empty-state-title")).toHaveText("No endpoints yet");
  await expectNoUnreviewedAccessibilityViolations(page, ".workspace-toolbar");
});

test("reflows aggregate totals within narrow activity panels without clipping", async ({ page }) => {
  await page.getByRole("button", { name: "Settings" }).click();
  const settings = page.getByRole("dialog", { name: "Settings" });
  await settings.getByLabel("Workspace layout").selectOption("columns");
  await settings.getByRole("button", { name: "Done" }).click();

  for (const width of [320, 600]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
    const padding = width <= 600 ? "16px" : "28px";
    await expect(page.locator("main")).toHaveCSS("padding-left", padding);
    await expect(page.locator("main")).toHaveCSS("padding-right", padding);
    const metrics = await page.locator(".metric:not(.rate-metric)").all();
    const bounds = await Promise.all(metrics.map((metric) => metric.boundingBox()));
    const rate = await page.locator(".rate-metric").boundingBox();
    const strip = await page.locator(".metric-strip").boundingBox();

    expect(bounds[0].y).toBe(bounds[1].y);
    expect(bounds[2].y).toBe(bounds[3].y);
    expect(bounds[2].y).toBeGreaterThanOrEqual(bounds[0].y + bounds[0].height);
    expect(rate.y).toBeGreaterThanOrEqual(bounds[2].y + bounds[2].height);
    for (const box of [...bounds, rate]) {
      expect(box.x).toBeGreaterThanOrEqual(strip.x);
      expect(box.x + box.width).toBeLessThanOrEqual(strip.x + strip.width);
    }
    expect(await page.locator(".metric-strip").evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
      true
    );
  }
});

test("keeps expanded endpoint and request log heights stable with keyboard-scrollable rows on roomy screens", async ({
  page,
  request,
}) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");
  await request.post("/__mockapi/api/statistics/reset");
  await expect(page.locator("#request-log-count")).toHaveText("0");
  const endpointsPanel = page.locator(".workspace");
  const logPanel = page.locator(".request-log-workspace");
  const emptyEndpoints = await endpointsPanel.boundingBox();
  const emptyLog = await logPanel.boundingBox();
  expect(emptyEndpoints.height).toBeGreaterThanOrEqual(700);
  expect(emptyLog.height).toBeGreaterThanOrEqual(400);
  expect(Math.abs(emptyEndpoints.y + emptyEndpoints.height - emptyLog.y - emptyLog.height)).toBeLessThanOrEqual(1);

  const endpoints = Array.from({ length: 25 }, (_, index) => ({
    id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
    name: `Scroll endpoint ${String(index + 1).padStart(2, "0")}`,
    enabled: true,
    methods: ["GET"],
    path: `/scroll/${index + 1}`,
    response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
  }));
  await importDocument(request, { ...emptyDocument, endpoints });
  await expect(page.locator("#endpoint-count")).toHaveText("25");
  await page.getByLabel("Rows per page").selectOption("100");
  for (const endpoint of endpoints) {
    expect((await request.get(endpoint.path)).status()).toBe(200);
  }
  await expect(page.locator("#request-log-count")).toHaveText("25");
  expect((await endpointsPanel.boundingBox()).height).toBe(emptyEndpoints.height);
  expect((await logPanel.boundingBox()).height).toBe(emptyLog.height);
  for (const [panel, selector] of [
    [endpointsPanel, "#endpoint-toggle"],
    [endpointsPanel, "#endpoint-pagination"],
    [logPanel, "#request-log-toggle"],
  ]) {
    const panelBounds = await panel.boundingBox();
    const controlBounds = await page.locator(selector).boundingBox();
    expect(controlBounds.x).toBeGreaterThanOrEqual(panelBounds.x);
    expect(controlBounds.x + controlBounds.width).toBeLessThanOrEqual(panelBounds.x + panelBounds.width);
    expect(controlBounds.y + controlBounds.height).toBeLessThanOrEqual(panelBounds.y + panelBounds.height);
  }

  for (const name of ["Endpoint registry", "Recent request log"]) {
    const region = page.getByRole("region", { name, exact: true });
    expect(await region.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);
    await region.focus();
    await page.keyboard.press("PageDown");
    await expect.poll(() => region.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  }
  await expect(page.getByLabel("Rows per page")).toBeVisible();
  await page.locator("#filter-text").fill("no matching endpoints");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(0);
  expect((await endpointsPanel.boundingBox()).height).toBe(emptyEndpoints.height);
  await page.locator("#filter-text").fill("");

  await page.getByRole("button", { name: "Collapse endpoints" }).click();
  await page.getByRole("button", { name: "Collapse request log" }).click();
  expect((await endpointsPanel.boundingBox()).height).toBeLessThan(emptyEndpoints.height);
  expect((await logPanel.boundingBox()).height).toBeLessThan(emptyLog.height);
  await page.getByRole("button", { name: "Expand endpoints" }).click();
  await page.getByRole("button", { name: "Expand request log" }).click();
  expect((await endpointsPanel.boundingBox()).height).toBe(emptyEndpoints.height);
  expect((await logPanel.boundingBox()).height).toBe(emptyLog.height);

  await page.setViewportSize({ width: 1600, height: 1200 });
  await expect(endpointsPanel).toHaveCSS("height", "840px");
  await expect(logPanel).toHaveCSS("height", "480px");
  for (const viewport of [
    { width: 1600, height: 700 },
    { width: 600, height: 1000 },
  ]) {
    await page.setViewportSize(viewport);
    expect((await endpointsPanel.boundingBox()).height).toBeGreaterThan(emptyEndpoints.height);
    expect((await logPanel.boundingBox()).height).toBeGreaterThan(emptyLog.height);
  }
});

test("persists the preferred dashboard layout from settings", async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await page.getByRole("button", { name: "Settings" }).click();

  const settings = page.getByRole("dialog", { name: "Settings" });
  const layout = settings.getByLabel("Workspace layout");
  await expect(layout).toHaveValue("auto");
  await layout.selectOption("stacked");
  await settings.getByRole("button", { name: "Done" }).click();

  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  await expect
    .poll(() => page.evaluate(() => JSON.parse(window.localStorage.getItem("mockapi.preferences")).dashboardLayout))
    .toBe("stacked");

  await page.setViewportSize({ width: 1200, height: 1000 });
  await page.getByRole("button", { name: "Settings" }).click();
  await layout.selectOption("columns");
  await settings.getByRole("button", { name: "Done" }).click();

  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  await expect
    .poll(() => page.evaluate(() => JSON.parse(window.localStorage.getItem("mockapi.preferences")).dashboardLayout))
    .toBe("columns");
  await page.setViewportSize({ width: 1920, height: 1000 });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");
  const endpoints = await page.locator(".workspace").boundingBox();
  const statistics = await page.locator(".statistics-workspace").boundingBox();
  expect(endpoints.x).toBeLessThan(statistics.x);
  expect(Math.abs(endpoints.width - statistics.width)).toBeLessThanOrEqual(1);
});

test("rechecks intrinsic command widths without changing saved layout or collapse choices", async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await page.getByRole("button", { name: "Settings" }).click();
  await page.getByLabel("Workspace layout").selectOption("columns");
  await page.getByRole("dialog", { name: "Settings" }).getByRole("button", { name: "Done" }).click();
  await page.locator("#statistics-toggle").click();
  await page.locator("#request-log-toggle").click();
  const saved = await page.evaluate(() => localStorage.getItem("mockapi.preferences"));
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");

  await page
    .locator(".command-group")
    .first()
    .evaluate((group) => {
      group.style.width = "1000px";
    });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
  await page
    .locator(".command-group")
    .first()
    .evaluate((group) => {
      group.style.removeProperty("width");
    });
  await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "columns");
  for (const id of ["statistics", "request-log"]) {
    await expect(page.locator(`#${id}-body`)).toBeHidden();
    await expect(page.locator(`#${id}-toggle`)).toHaveAttribute("aria-expanded", "false");
  }
  expect(await page.evaluate(() => localStorage.getItem("mockapi.preferences"))).toBe(saved);
});

test("remains usable when the localStorage getter is blocked", async ({ context }) => {
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.addInitScript(() => {
    Object.defineProperty(window, "localStorage", {
      get() {
        throw new DOMException("Storage blocked", "SecurityError");
      },
    });
  });
  try {
    await page.setViewportSize({ width: 1920, height: 1000 });
    await page.goto("/?tutorial=skip");
    await expect(page.locator("#connection-status")).toHaveText("Live");
    await expect(page.locator("#statistics-body")).toBeVisible();
    await expect(page.locator("#request-log-body")).toBeVisible();
    await page.getByRole("button", { name: "Settings" }).click();
    await page.getByLabel("Workspace layout").selectOption("stacked");
    await page.getByRole("dialog", { name: "Settings" }).getByRole("button", { name: "Done" }).click();
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", "stacked");
    await page.locator("#statistics-toggle").click();
    await expect(page.locator("#statistics-body")).toBeHidden();
    expect(errors).toEqual([]);
  } finally {
    await page.close();
  }
});

test("reflows without page overflow", async ({ page }) => {
  await expect(page.locator("html")).toHaveCSS("overflow-y", "scroll");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth))
    .toBe(true);
  await page.getByRole("button", { name: "Test" }).first().click();
  const bladeWidth = await page.locator("#test-blade").evaluate((element) => element.getBoundingClientRect().width);
  const viewportWidth = await page.evaluate(() => document.documentElement.clientWidth);
  expect(bladeWidth).toBeLessThanOrEqual(viewportWidth + 1);
});
