import { expect, test, expectNoUnreviewedAccessibilityViolations } from "./dashboard-fixtures.js";

test("integrates aggregate totals and rate into Statistics without a miniature chart", async ({ page }) => {
  await expect(page.locator("#statistics-body .metric-strip")).toHaveCount(1);
  await expect(page.locator("#rate-bars")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Reset statistics", exact: true })).toHaveCount(1);
  for (const width of [900, 1200, 1600, 1920]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", width >= 1760 ? "columns" : "stacked");
    const metrics = await page.locator(".metric").all();
    const bounds = await Promise.all(metrics.map((metric) => metric.boundingBox()));
    const strip = await page.locator(".metric-strip").boundingBox();

    for (const metric of bounds) {
      expect(metric.y).toBe(bounds[0].y);
      expect(metric.x).toBeGreaterThanOrEqual(strip.x);
      expect(metric.x + metric.width).toBeLessThanOrEqual(strip.x + strip.width);
    }
    expect(strip.height).toBeLessThanOrEqual(100);
  }

  await page.getByRole("button", { name: "Reset statistics", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Reset overall statistics" })).toBeVisible();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.locator("#reset-statistics-scope")).toBeFocused();
});

test("keeps the Statistics layout stable when changing scope", async ({ page }) => {
  const statistics = page.locator(".statistics-workspace");
  const toolbar = page.locator(".statistics-toolbar");
  const endpointPicker = page.locator("#statistics-endpoint-picker");

  for (const width of [600, 900, 1600]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.getByRole("button", { name: "Overall", exact: true }).click();
    await expect(endpointPicker).toBeHidden();
    const overallBounds = {
      statistics: await statistics.boundingBox(),
      toolbar: await toolbar.boundingBox(),
    };

    await page.getByRole("button", { name: "By endpoint", exact: true }).click();
    await expect(endpointPicker).toBeVisible();
    const endpointBounds = {
      statistics: await statistics.boundingBox(),
      toolbar: await toolbar.boundingBox(),
    };

    expect(endpointBounds.statistics).toEqual(overallBounds.statistics);
    expect(endpointBounds.toolbar).toEqual(overallBounds.toolbar);
  }
});

test("uses the consolidated Statistics reset control for overall and endpoint scopes", async ({ page, request }) => {
  await request.post("/__mockapi/api/statistics/reset");
  await page.getByRole("button", { name: "By endpoint", exact: true }).click();
  await expect(page.getByRole("button", { name: "Reset endpoint", exact: true })).toBeDisabled();
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await page
    .getByLabel("Endpoint statistics")
    .selectOption({ label: "Check Cloud Cruiser wait times · /ctp/attractions/cloud-cruiser/wait-times" });
  expect((await request.get("/ctp/attractions/cloud-cruiser/wait-times")).status()).toBe(200);
  await expect(page.locator("#metric-total")).toHaveText("1");
  const endpointId = await page.getByLabel("Endpoint statistics").inputValue();
  await page.getByRole("button", { name: "Reset endpoint", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Reset endpoint statistics" })).toBeVisible();
  const endpointReset = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/statistics/endpoints/${endpointId}/reset`) && response.request().method() === "POST"
  );
  await page.locator("#confirm-accept").click();
  expect((await endpointReset).ok()).toBe(true);

  await page.getByRole("button", { name: "Overall", exact: true }).click();
  await page.getByRole("button", { name: "Reset statistics", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Reset overall statistics" })).toBeVisible();
  const overallReset = page.waitForResponse(
    (response) => response.url().endsWith("/statistics/reset") && response.request().method() === "POST"
  );
  await page.locator("#confirm-accept").click();
  expect((await overallReset).ok()).toBe(true);
  await expect(page.locator("#metric-total")).toHaveText("0");
  await expect(page.locator("#metric-matched")).toHaveText("0");
  await expect(page.locator("#metric-unmatched")).toHaveText("0");
  await expect(page.locator("#metric-bytes")).toHaveText("0 B");
  await expect(page.locator("#metric-rate")).toHaveText("0.0 attempts/min");
});

test("shows only configured endpoint requests without retaining query values", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);
  await request.post("/__mockapi/api/statistics/reset");

  const matchedResponse = await request.get("/ctp/parks?token=private-value");
  expect(matchedResponse.status()).toBe(200);
  const unmatchedResponse = await request.get("/missing-latest?token=private-value");
  expect(unmatchedResponse.status()).toBe(404);

  const log = page.getByRole("region", { name: "Recent request log" });
  const rows = log.locator("tbody .request-log-entry");
  await expect(rows).toHaveCount(1);
  await expect(log.getByRole("columnheader", { name: "Local time" })).toBeVisible();
  await expect(log.getByRole("columnheader", { name: "UTC" })).toBeVisible();
  await page.setViewportSize({ width: 1200, height: 1000 });
  expect(
    await log
      .locator("thead th")
      .evaluateAll((headers) => headers.every((header) => getComputedStyle(header).whiteSpace === "nowrap"))
  ).toBe(true);
  await expect(log.locator(".request-log-bucket")).toHaveCount(1);
  await expect(log.locator(".request-log-bucket")).toContainText("Bucket:");
  const bucketToggle = log.locator(".request-log-bucket-toggle");
  await expect(bucketToggle).toHaveAttribute("aria-expanded", "true");
  await expect(bucketToggle).toContainText("1 request");
  await bucketToggle.click();
  await expect(bucketToggle).toHaveAttribute("aria-expanded", "false");
  await expect(rows.nth(0)).toBeHidden();
  await bucketToggle.click();
  await expect(rows.nth(0)).toBeVisible();
  await expect(rows.nth(0)).toContainText("Discover the parks");
  await expect(rows.nth(0)).toContainText("/ctp/parks");
  await expect(rows.nth(0).locator(".request-log-result")).toHaveClass(/status-2xx/);
  await expect(rows.nth(0).locator(".request-log-result")).toHaveCSS("text-align", "center");
  await expect(page.locator("#metric-unmatched")).toHaveText("1");
  await expect(log).not.toContainText("/missing-latest");
  await expect(log).not.toContainText("private-value");
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("collapses request log buckets and opens them from statistics columns", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);
  await request.post("/__mockapi/api/statistics/reset");
  const response = await request.get("/ctp/parks");
  expect(response.status()).toBe(200);

  const bucketToggle = page.locator(".request-log-bucket-toggle");
  await expect(bucketToggle).toHaveCount(1);
  await bucketToggle.click();
  await expect(bucketToggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".request-log-entry:visible")).toHaveCount(0);

  await page.getByRole("button", { name: "Collapse request log" }).click();
  const activeColumn = page.locator("#statistics-chart button.chart-column.active").last();
  await expect(activeColumn).toHaveAccessibleName(/Show .* in the request log/);
  await activeColumn.click();

  await expect(page.locator("#request-log-body")).toBeVisible();
  await expect(page.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator(".request-log-entry").first()).toBeVisible();
  await expect(page.locator(".request-log-bucket-toggle")).toBeFocused();
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("bulk bucket controls preserve column widths through collapse, expansion, and live updates", async ({
  page,
  request,
}) => {
  const configuration = await (await request.get("/__mockapi/api/configuration")).json();
  const statistics = await (await request.get("/__mockapi/api/statistics")).json();
  await page.addInitScript(() => {
    window.EventSource = class extends EventTarget {
      constructor() {
        super();
        window.requestLogEvents = this;
      }
      close() {}
    };
  });
  await page.reload();
  await expect.poll(() => page.evaluate(() => Boolean(window.requestLogEvents))).toBe(true);
  await page.evaluate((configuration) => {
    window.requestLogEvents.onopen();
    window.requestLogEvents.dispatchEvent(new MessageEvent("configuration", { data: JSON.stringify(configuration) }));
  }, configuration);

  async function publishRequests(recentRequests) {
    await page.evaluate(
      (statistics) => {
        window.requestLogEvents.dispatchEvent(new MessageEvent("statistics", { data: JSON.stringify(statistics) }));
      },
      { ...statistics, recentRequests }
    );
    await expect(page.locator("#request-log-count")).toHaveText(String(recentRequests.length));
  }

  const control = page.locator("#request-log-toggle-buckets");
  const log = page.getByRole("region", { name: "Recent request log", exact: true });
  const columns = () =>
    log.locator("thead th").evaluateAll((headers) =>
      headers.map((header) => ({
        left: header.offsetLeft,
        width: header.getBoundingClientRect().width,
      }))
    );
  await publishRequests([]);
  await expect(control).toHaveText("Collapse all buckets");
  await expect(control).toBeDisabled();
  const emptyColumns = await columns();
  const requests = Array.from({ length: 12 }, (_, index) => ({
    timestampUtc: `2026-09-26T12:${index < 6 ? "02" : "01"}:${String(index % 6).padStart(2, "0")}Z`,
    method: index === 0 ? "CUSTOM-METHOD-WITH-A-LONG-NAME" : "GET",
    path: `/request-log/${"long-path-segment".repeat(12)}/${index}`,
    endpointId: null,
    statusCode: 404,
    outcome: "unmatched",
    responseBytes: 0,
    transportAttempts: 1,
  }));
  await publishRequests(requests);
  await expect(log.locator(".request-log-bucket-toggle")).toHaveCount(2);
  expect(await columns()).toEqual(emptyColumns);
  const controlWidth = (await control.boundingBox()).width;
  expect(
    await log
      .locator(".request-log-entry td")
      .evaluateAll((cells) => cells.every((cell) => cell.scrollWidth <= cell.clientWidth + 1))
  ).toBe(true);

  await control.focus();
  await page.keyboard.press("Enter");
  await expect(control).toHaveText("Expand all buckets");
  await expect(control).toBeFocused();
  await expect(log.locator(".request-log-entry:visible")).toHaveCount(0);
  await expect(log.locator('.request-log-bucket-toggle[aria-expanded="false"]')).toHaveCount(2);
  expect(await columns()).toEqual(emptyColumns);
  expect((await control.boundingBox()).width).toBe(controlWidth);
  await page.keyboard.press("Enter");
  await expect(log.locator(".request-log-entry:visible")).toHaveCount(12);
  expect(await columns()).toEqual(emptyColumns);

  await log.locator(".request-log-bucket-toggle").first().click();
  await expect(log.locator('.request-log-bucket-toggle[aria-expanded="false"]')).toHaveCount(1);
  await expect(control).toHaveText("Collapse all buckets");
  expect(await columns()).toEqual(emptyColumns);
  await control.click();
  await expect(log.locator(".request-log-entry:visible")).toHaveCount(0);

  const additional = { ...requests[0], timestampUtc: "2026-09-26T12:02:30Z" };
  await publishRequests([additional, ...requests]);
  await expect(log.locator(".request-log-entry:visible")).toHaveCount(0);
  await expect(control).toHaveText("Expand all buckets");
  const newBucket = { ...additional, timestampUtc: "2026-09-26T12:03:00Z" };
  await publishRequests([newBucket, additional, ...requests]);
  await expect(log.locator(".request-log-entry:visible")).toHaveCount(1);
  await expect(control).toHaveText("Collapse all buckets");
  expect(await columns()).toEqual(emptyColumns);
  await expectNoUnreviewedAccessibilityViolations(page, ".request-log-workspace");

  await publishRequests([]);
  await expect(control).toBeDisabled();
  expect(await columns()).toEqual(emptyColumns);
  await publishRequests(requests);
  await expect(log.locator(".request-log-entry:visible")).toHaveCount(12);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(
    true
  );
  for (const viewport of [
    { width: 1600, height: 1000 },
    { width: 600, height: 1000 },
  ]) {
    await page.setViewportSize(viewport);
    const expandedColumns = await columns();
    await control.click();
    expect(await columns()).toEqual(expandedColumns);
    await control.click();
    expect(await columns()).toEqual(expandedColumns);
  }
});

test("shows grounded endpoint statistics as a graph and table", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(
    page.locator("#endpoint-rows").getByRole("row", { name: /Check Cloud Cruiser wait times/ })
  ).toBeVisible();
  await request.post("/__mockapi/api/statistics/reset");
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const response = await request.get("/ctp/attractions/cloud-cruiser/wait-times");
    expect(response.status()).toBe(200);
  }
  const limitedResponse = await request.get("/ctp/attractions/cloud-cruiser/wait-times");
  expect(limitedResponse.status()).toBe(429);

  await page.getByRole("button", { name: "By endpoint" }).click();
  await page
    .getByLabel("Endpoint statistics")
    .selectOption({ label: "Check Cloud Cruiser wait times · /ctp/attractions/cloud-cruiser/wait-times" });
  await expect(page.locator("#statistics-chart-title")).toHaveText("Check Cloud Cruiser wait times attempt activity");
  await expect(page.locator("#statistics-chart")).toHaveAttribute(
    "aria-label",
    /The first 4 requests within 10 seconds return HTTP 200; later requests return HTTP 429\./
  );
  const chartScroll = page.getByRole("region", { name: /Attempt activity timeline/ });
  const chartViewport = await chartScroll.evaluate((element) => ({
    clientWidth: element.clientWidth,
    overflowX: getComputedStyle(element).overflowX,
    scrollLeft: element.scrollLeft,
    scrollWidth: element.scrollWidth,
  }));
  expect(chartViewport.overflowX).toBe("auto");
  expect(chartViewport.scrollWidth).toBe(chartViewport.clientWidth);
  expect(chartViewport.scrollLeft).toBe(0);
  await expect(page.locator(".chart-axis span")).toHaveText(["60 minutes ago", "30 minutes ago", "Now"]);
  const responseStatusLegendRowCount = await page
    .locator(".chart-legend span")
    .evaluateAll((items) => new Set(items.map((item) => item.offsetTop)).size);
  expect(responseStatusLegendRowCount).toBe(1);
  const activeColumn = page.locator("#statistics-chart .chart-column.active").last();
  await expect(activeColumn.locator(".status-2xx")).toHaveCSS("flex-grow", "4");
  await expect(activeColumn.locator(".status-4xx")).toHaveCSS("flex-grow", "1");
  await expect(activeColumn.locator(".status-2xx")).toHaveCSS("background-color", /rgb\((22, 114, 58|114, 214, 154)\)/);
  await expect(activeColumn.locator(".status-4xx")).toHaveCSS("background-color", /rgb\((189, 30, 44|255, 138, 150)\)/);
  await activeColumn.hover();
  const tooltip = page.locator(".chart-tooltip");
  await expect(tooltip).toBeVisible();
  await expect(tooltip.locator(".chart-tooltip-heading")).toContainText("5 attempts");
  await expect(tooltip.locator(".chart-tooltip-row")).toHaveText(["1xx0", "2xx4", "3xx0", "4xx1", "5xx0", "Other0"]);
  await expect(tooltip.locator(".status-2xx")).toHaveCSS("background-color", /rgb\((22, 114, 58|114, 214, 154)\)/);
  await expect(tooltip.locator(".status-4xx")).toHaveCSS("background-color", /rgb\((189, 30, 44|255, 138, 150)\)/);

  const requestLogBucket = page.locator(".request-log-bucket-toggle").first();
  await requestLogBucket.click();
  await expect(requestLogBucket).toHaveAttribute("aria-expanded", "false");
  await page.getByRole("button", { name: "Collapse request log" }).click();
  await activeColumn.click();
  await expect(page.getByRole("button", { name: "Collapse request log" })).toHaveAttribute("aria-expanded", "true");
  const revealedBucket = page.locator(".request-log-bucket-toggle").first();
  await expect(revealedBucket).toHaveAttribute("aria-expanded", "true");
  await expect(revealedBucket).toBeFocused();
  await expect(page.locator(".request-log-entry").first()).toBeVisible();

  await page.getByRole("button", { name: "Table", exact: true }).click();
  const statisticsTable = page.getByRole("region", { name: "Detailed attempt statistics" });
  await expect(statisticsTable.getByRole("columnheader", { name: "Attempts" })).toBeVisible();
  const statisticsRow = page
    .locator("#statistics-table-view tbody tr")
    .filter({ hasText: "Check Cloud Cruiser wait times" });
  await expect(statisticsRow).toContainText("5");
  await expect(statisticsRow).toContainText("attempts/min");
  await page
    .locator(".request-log-entry")
    .filter({ hasText: "/ctp/attractions/cloud-cruiser/wait-times" })
    .filter({ has: page.locator(".status-4xx") })
    .hover();
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("@smoke collapses statistics without losing the selected view", async ({ page }) => {
  await page.getByRole("button", { name: "Table", exact: true }).click();
  await expect(page.locator("#statistics-table-view")).toBeVisible();

  const collapse = page.getByRole("button", { name: "Collapse statistics" });
  await collapse.click();
  await expect(page.locator("#statistics-body")).toBeHidden();
  await expect(page.locator(".metric-strip")).toBeHidden();
  await expect(page.locator("#statistics-controls")).toBeHidden();
  await expect(page.getByRole("button", { name: "Expand statistics" })).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator("#statistics-toggle svg")).toHaveCSS("transform", "matrix(0, 1, -1, 0, 0, 0)");
  await expectNoUnreviewedAccessibilityViolations(page);

  const expand = page.getByRole("button", { name: "Expand statistics" });
  await expand.click();
  await expect(page.locator("#statistics-table-view")).toBeVisible();
  await expect(page.locator(".metric-strip")).toBeVisible();
  await expect(page.locator("#statistics-graph-view")).toBeHidden();
  await expect(page.locator("#statistics-toggle")).toBeFocused();
});

test("@smoke collapses the request log and restores the preference", async ({ page }) => {
  await page.getByRole("button", { name: "Collapse request log" }).click();
  await expect(page.locator("#request-log-body")).toBeHidden();
  await expect(page.getByRole("button", { name: "Expand request log" })).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator("#request-log-toggle svg")).toHaveCSS("transform", "matrix(0, 1, -1, 0, 0, 0)");
  await expectNoUnreviewedAccessibilityViolations(page);

  await page.reload();
  const expand = page.getByRole("button", { name: "Expand request log" });
  await expect(expand).toHaveAttribute("aria-expanded", "false");
  await expand.click();
  await expect(page.getByRole("region", { name: "Recent request log" })).toBeVisible();
  await expect(page.locator("#request-log-toggle")).toBeFocused();
});
