import { expect, test, emptyDocument, importDocument } from "./dashboard-fixtures.js";

const endpoint = {
  id: "ca30f090-d19e-431a-93a9-d7000851ad4c",
  name: "Live endpoint",
  enabled: true,
  methods: ["GET"],
  path: "/live-endpoint",
  response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "live response" },
};

test("keeps management commands pending through the authoritative refresh", async ({ page }) => {
  let releaseRefresh;
  const refreshGate = new Promise((resolve) => {
    releaseRefresh = resolve;
  });
  let refreshStarted = false;
  const writes = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().includes("/__mockapi/api/")) writes.push(request);
  });
  await page.route("**/__mockapi/api/configuration", async (route) => {
    refreshStarted = true;
    await refreshGate;
    await route.continue();
  });
  try {
    await page.getByRole("button", { name: "New endpoint", exact: true }).click();
    await page.locator("#field-name").fill("Refresh-bound command");
    await page.locator("#field-path").fill("/refresh-bound");
    await page.getByRole("button", { name: "Apply endpoint" }).click();
    await expect.poll(() => refreshStarted).toBe(true);
    await expect(page.locator("#endpoint-dialog")).toBeVisible();
    await expect(page.getByRole("button", { name: "Apply endpoint" })).toBeDisabled();
    for (const selector of ["#load-example-button", "#import-button", "#save-button"]) {
      await expect(page.locator(selector)).toBeDisabled();
    }
    await page.locator("#load-example-button").dispatchEvent("click");
    await expect(page.locator("#toast-region .error")).toContainText("already in progress");
    expect(writes).toHaveLength(1);
    expect(new URL(writes[0].url()).pathname).toBe("/__mockapi/api/endpoints");
  } finally {
    releaseRefresh();
  }
  await expect(page.locator("#endpoint-dialog")).toBeHidden();
  await expect(page.locator(".endpoint-row")).toContainText("Refresh-bound command");
  await expect(page.locator("#load-example-button")).toBeEnabled();
  await expect(page.locator("#save-button")).toBeDisabled();
  expect(writes).toHaveLength(1);
});

test("surfaces a failed refresh without replaying a successful write and recovers on reload", async ({
  page,
  request,
}) => {
  const writes = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/endpoints")) writes.push(request);
  });
  await page.route("**/__mockapi/api/configuration", (route) =>
    route.fulfill({
      status: 503,
      contentType: "application/problem+json",
      body: JSON.stringify({ detail: "Refresh unavailable" }),
    })
  );
  await page.getByRole("button", { name: "New endpoint", exact: true }).click();
  await page.locator("#field-name").fill("Committed before refresh failed");
  await page.locator("#field-path").fill("/refresh-failure");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.locator("#toast-region .error")).toHaveText("Refresh unavailable");
  await expect(page.locator("#endpoint-dialog")).toBeVisible();
  await expect(page.getByRole("button", { name: "Apply endpoint" })).toBeEnabled();
  await expect(page.locator("#field-name")).toHaveValue("Committed before refresh failed");
  await expect(page.locator("#import-button")).toBeEnabled();
  const definitions = await (await request.get("/__mockapi/api/endpoints")).json();
  expect(definitions).toHaveLength(1);
  expect(definitions[0].name).toBe("Committed before refresh failed");

  await page.unroute("**/__mockapi/api/configuration");
  await page.reload();
  await expect(page.locator("#connection-status")).toHaveText("Live");
  await expect(page.locator(".endpoint-row")).toHaveCount(1);
  await expect(page.locator(".endpoint-row")).toContainText(definitions[0].name);
  expect(writes).toHaveLength(1);
});

test("reports an interrupted command refresh when hidden and resumes without replaying the write", async ({ page }) => {
  let releaseRefresh;
  const refreshGate = new Promise((resolve) => {
    releaseRefresh = resolve;
  });
  let refreshStarted = false;
  let writes = 0;
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/endpoints")) writes++;
  });
  await page.route("**/__mockapi/api/configuration", async (route) => {
    refreshStarted = true;
    await refreshGate;
    await route.continue();
  });
  try {
    await page.getByRole("button", { name: "New endpoint", exact: true }).click();
    await page.locator("#field-name").fill("Committed before hiding");
    await page.locator("#field-path").fill("/hidden-refresh");
    await page.getByRole("button", { name: "Apply endpoint" }).click();
    await expect.poll(() => refreshStarted).toBe(true);
    await page.evaluate(() => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await expect(page.locator("#toast-region .error")).toContainText("Dashboard refresh was interrupted");
    await expect(page.locator("#endpoint-dialog")).toBeVisible();
    await expect(page.getByRole("button", { name: "Apply endpoint" })).toBeEnabled();
  } finally {
    releaseRefresh();
    await page.unrouteAll({ behavior: "wait" });
    await page.evaluate(() => {
      delete document.visibilityState;
      document.dispatchEvent(new Event("visibilitychange"));
    });
  }
  await page.locator("#endpoint-dialog").press("Escape");
  await expect(page.locator(".endpoint-row")).toHaveCount(1);
  await expect(page.locator(".endpoint-row")).toContainText("Committed before hiding");
  expect(writes).toBe(1);
});

test("keeps table selection and statistics scope by stable ID through live edits and deletion", async ({
  page,
  request,
}) => {
  const second = {
    ...endpoint,
    id: "a85ef6f3-e091-4cfc-98d7-57285ef80af2",
    name: "Second endpoint",
    path: "/second-endpoint",
  };
  await importDocument(request, { ...emptyDocument, endpoints: [endpoint, second] });
  const row = page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`);
  await row.getByRole("checkbox", { name: `Select ${endpoint.name}`, exact: true }).check();
  await page.locator("#statistics-endpoint").click();
  await page.locator("#statistics-endpoint-select").selectOption(endpoint.id);
  expect((await request.get(endpoint.path)).status()).toBe(200);
  await expect(row.locator("[data-endpoint-requests]")).toHaveText("1");
  await expect(page.locator(".request-log-entry")).toContainText(endpoint.name);

  const renamed = { ...endpoint, name: "Renamed live endpoint" };
  await importDocument(request, { ...emptyDocument, endpoints: [renamed, second] });
  await expect(row).toContainText(renamed.name);
  await expect(row.locator(".selection-checkbox")).toBeChecked();
  await expect(page.locator("#selection-count")).toHaveText("1");
  await expect(page.locator("#statistics-endpoint-select")).toHaveValue(endpoint.id);
  await expect(page.locator("#statistics-chart-title")).toHaveText(`${renamed.name} attempt activity`);
  await expect(page.locator(".request-log-entry")).toContainText(renamed.name);

  await importDocument(request, { ...emptyDocument, endpoints: [second] });
  await expect(row).toHaveCount(0);
  await expect(page.locator("#selection-count")).toHaveText("0");
  await expect(page.locator("#statistics-endpoint-select")).toHaveValue(second.id);
  await expect(page.locator("#statistics-chart-title")).toHaveText(`${second.name} attempt activity`);
  await expect(page.locator("[data-endpoint-requests]")).toHaveText("0");
});

test("preserves an open test blade through live edits and restores focus to the replacement row", async ({
  page,
  request,
}) => {
  await importDocument(request, { ...emptyDocument, endpoints: [endpoint] });
  const row = page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`);
  await row.getByRole("button", { name: "Test", exact: true }).click();
  const renamed = { ...endpoint, name: "Updated while testing" };
  await importDocument(request, { ...emptyDocument, endpoints: [renamed] });
  await expect(row).toContainText(renamed.name);
  await expect(page.locator("#test-blade-title")).toHaveText(endpoint.name);
  await expect(page.locator("#test-path")).toHaveValue(endpoint.path);
  await expect(page.locator("#test-method")).toBeFocused();
  await page.locator("#test-send").click();
  await expect(page.locator("#test-response-body")).toHaveText(endpoint.response.body);
  await page.locator("#test-blade-close").click();
  await expect(page.locator("#test-blade-shell")).toBeHidden();
  await expect(row.getByRole("button", { name: "Test", exact: true })).toBeFocused();
});

test("@smoke dashboard synchronization is event-driven through idle time, external edits, and automatic saves", async ({
  page,
  request,
}) => {
  const reads = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (
      request.method() === "GET" &&
      ["/__mockapi/api/configuration", "/__mockapi/api/statistics", "/__mockapi/api/endpoints"].includes(path)
    ) {
      reads.push(path);
    }
  });
  await page.clock.install();
  await page.reload();
  await expect.poll(() => reads.filter((path) => path.endsWith("/endpoints")).length).toBe(1);
  await expect(page.locator("#connection-status")).toHaveText("Live");
  await page.clock.runFor(60000);
  expect(reads).toEqual(["/__mockapi/api/endpoints"]);

  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  await importDocument(request, example);
  await expect(page.locator(".endpoint-row")).not.toHaveCount(0);
  await expect(page.getByRole("button", { name: "Retry save", exact: true })).toBeDisabled();
  expect(reads).toEqual(["/__mockapi/api/endpoints", "/__mockapi/api/endpoints"]);
  const configuration = await (await request.get("/__mockapi/api/configuration")).json();
  expect(configuration.hasUnsavedChanges).toBe(false);
  expect(
    (await request.post("/__mockapi/api/configuration/save", { headers: { "If-Match": configuration.etag } })).ok()
  ).toBe(true);
  await expect(page.getByRole("button", { name: "Retry save", exact: true })).toBeDisabled();
  expect(reads).toEqual(["/__mockapi/api/endpoints", "/__mockapi/api/endpoints"]);
});

test("statistics updates preserve endpoint controls and render collapsed content when reopened", async ({
  page,
  request,
}) => {
  await request.post("/__mockapi/api/statistics/reset");
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  await importDocument(request, example);
  const endpoint = example.endpoints.find((endpoint) => endpoint.enabled && endpoint.methods.includes("GET"));
  const row = page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`);
  await expect(row).toBeVisible();
  const control = row.getByRole("button", { name: "Edit", exact: true });
  await control.focus();
  await control.evaluate((element) => {
    window.originalEndpointControl = element;
  });
  await request.get(endpoint.path);
  await expect(row.locator("[data-endpoint-requests]")).toHaveText("1");
  expect(await control.evaluate((element) => element === window.originalEndpointControl)).toBe(true);
  await expect(control).toBeFocused();

  await page.locator("#endpoint-toggle").click();
  await request.get(endpoint.path);
  await expect(page.locator("#metric-total")).toHaveText("2");
  await page.locator("#endpoint-toggle").click();
  await expect(row.locator("[data-endpoint-requests]")).toHaveText("2");
});

test("keeps the viewport fixed when live status updates rerender a focused endpoint action", async ({
  page,
  request,
}) => {
  await request.post("/__mockapi/api/statistics/reset");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.locator("#endpoint-rows").getByRole("row", { name: /Hello from MockAPI/ });
  const testButton = row.getByRole("button", { name: "Test" });
  await page.evaluate(() => window.scrollTo(0, 0));
  await testButton.evaluate((button) => button.focus({ preventScroll: true }));
  expect(await page.evaluate(() => window.scrollY)).toBe(0);

  await request.get("/ex/hello");
  await expect(row.locator("td").nth(5)).toHaveText("1");
  await expect(testButton).toBeFocused();
  expect(await page.evaluate(() => window.scrollY)).toBe(0);
});

test("suspends cached pages, resumes their controllers, and disposes discarded pages", async ({ page }) => {
  await page.addInitScript(() => {
    window.controllerStreams = [];
    window.EventSource = class extends EventTarget {
      constructor() {
        super();
        this.closed = false;
        window.controllerStreams.push(this);
      }
      close() {
        this.closed = true;
      }
    };
  });
  await page.reload();
  await expect.poll(() => page.evaluate(() => window.controllerStreams.length)).toBe(1);
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: true })));
  expect(await page.evaluate(() => window.controllerStreams[0].closed)).toBe(true);
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true })));
  await expect.poll(() => page.evaluate(() => window.controllerStreams.length)).toBe(2);
  await page.getByRole("button", { name: "New endpoint", exact: true }).click();
  await expect(page.locator("#endpoint-dialog")).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: false })));
  expect(await page.evaluate(() => window.controllerStreams.every((stream) => stream.closed))).toBe(true);
  await expect(page.locator("#endpoint-dialog")).toBeHidden();
  await page.getByRole("button", { name: "New endpoint", exact: true }).click();
  await expect(page.locator("#endpoint-dialog")).toBeHidden();
});

test("shows a disconnected state when management requests fail", async ({ page }) => {
  await page.route("**/__mockapi/api/**", (route) => route.abort());
  await page.reload();
  const connectionStatus = page.locator("#connection-status");
  await expect(connectionStatus).toContainText("Disconnected");
  await expect(connectionStatus).toHaveAttribute(
    "title",
    "Dashboard disconnected: configuration and statistics updates are currently unavailable."
  );
});

test("reports polling mode when EventSource is unavailable", async ({ page }) => {
  const warnings = [];
  page.on("console", (message) => {
    if (message.type() === "warning") warnings.push(message.text());
  });
  await page.addInitScript(() => delete window.EventSource);

  await page.reload();

  const connectionStatus = page.locator("#connection-status");
  await expect(connectionStatus).toContainText("Live");
  await expect(connectionStatus).toHaveAttribute(
    "title",
    "Dashboard connected: configuration and statistics updates are available."
  );
  await expect(connectionStatus.locator("span")).toHaveCSS("background-color", /rgb\((33, 135, 57|85, 189, 106)\)/);
  expect(warnings).toContain("Server-sent events are unavailable. Dashboard updates will use polling.");
});

test("loads without unexpected console or page errors", async ({ page }) => {
  const errors = [];
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => errors.push(`page: ${error.message}`));
  await page.reload();
  await expect(page.locator("#connection-status")).toContainText("Live");
  expect(errors).toEqual([]);
});
