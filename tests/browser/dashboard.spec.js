import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

const emptyDocument = {
  $schema: "../schemas/mockapi.schema.json",
  schemaVersion: "1.0",
  endpoints: [],
};

async function resetConfiguration(request) {
  const status = await request.get("/__mockapi/api/configuration");
  const configuration = await status.json();
  const response = await request.put("/__mockapi/api/configuration/import", {
    headers: { "If-Match": configuration.etag },
    data: emptyDocument,
  });
  expect(response.ok()).toBeTruthy();
}

async function importDocument(request, document) {
  const status = await request.get("/__mockapi/api/configuration");
  const configuration = await status.json();
  const response = await request.put("/__mockapi/api/configuration/import", {
    headers: { "If-Match": configuration.etag },
    data: document,
  });
  expect(response.ok()).toBeTruthy();
}

async function expectNoUnreviewedAccessibilityViolations(page) {
  const results = await new AxeBuilder({ page }).analyze();
  const violations = results.violations.filter((violation) =>
    ["moderate", "serious", "critical"].includes(violation.impact)
  );
  expect(violations, JSON.stringify(violations, null, 2)).toEqual([]);
}

test.beforeEach(async ({ page, request }) => {
  await resetConfiguration(request);
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Endpoint configuration" })).toBeVisible();
});

test("@smoke loads examples idempotently and filters by status", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows tr")).toHaveCount(4);
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.getByText("No example changes were needed")).toBeVisible();
  await expect(page.locator("#endpoint-rows tr")).toHaveCount(4);

  await page.locator("#filter-status").selectOption("4");
  await expect(page.locator("#endpoint-rows tr")).toHaveCount(1);
  await expect(page.locator("#endpoint-rows").getByText("Rate limited response", { exact: true })).toBeVisible();
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("creates, edits, disables, and deletes an endpoint", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await page.locator("#field-name").fill("Browser endpoint");
  await page.locator("#field-path").fill("/browser-test");
  await page.locator("#field-body").fill("created in browser");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  const row = page.getByRole("row", { name: /Browser endpoint/ });
  await expect(row).toBeVisible();

  await row.getByRole("button", { name: "Edit" }).click();
  await page.locator("#field-name").fill("Edited browser endpoint");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  const editedRow = page.getByRole("row", { name: /Edited browser endpoint/ });
  await expect(editedRow).toBeVisible();
  await editedRow.getByRole("checkbox").uncheck();
  await expect(editedRow.getByRole("checkbox")).not.toBeChecked();
  await editedRow.getByRole("button", { name: "Delete" }).click();
  await page.getByRole("button", { name: "Confirm" }).click();
  await expect(editedRow).toHaveCount(0);
});

test("imports, saves, and exports configuration", async ({ page }) => {
  const endpoint = {
    id: "ec97de43-59ec-49f6-a3f5-77523adb275d",
    name: "Imported endpoint",
    enabled: true,
    methods: ["GET"],
    path: "/imported-browser",
    response: {
      statusCode: 200,
      reasonPhrase: "OK",
      headers: {},
      contentType: "text/plain; charset=utf-8",
      body: "imported",
    },
  };
  await page.locator("#import-file").setInputFiles({
    name: "mockapi.json",
    mimeType: "application/json",
    buffer: Buffer.from(JSON.stringify({ ...emptyDocument, endpoints: [endpoint] })),
  });
  await page.getByRole("button", { name: "Confirm" }).click();
  await expect(page.getByRole("row", { name: /Imported endpoint/ })).toBeVisible();
  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.locator("#persistence-label")).toContainText("Persisted");

  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("link", { name: "Export" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe("mockapi.json");
});

test("reports server-authoritative validation errors", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await page.locator("#field-name").fill("Reserved endpoint");
  await page.locator("#field-path").fill("/health/live");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.getByRole("alert")).toContainText("reserved");
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("previews a built-in conflict and force-updates only reviewed endpoints", async ({ page, request }) => {
  const exampleResponse = await request.get("/__mockapi/api/configuration/example");
  const example = await exampleResponse.json();
  const changedHello = structuredClone(example.endpoints.find((endpoint) => endpoint.path === "/ex/hello"));
  changedHello.response.body = "changed locally";
  await importDocument(request, { ...emptyDocument, endpoints: [changedHello] });
  await page.reload();

  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.getByRole("heading", { name: "Built-in configuration conflicts" })).toBeVisible();
  await expect(page.locator("#confirm-message")).toContainText("Hello from MockAPI conflicts");
  await expectNoUnreviewedAccessibilityViolations(page);
  await page.getByRole("button", { name: "Force update" }).click();
  await expect(page.locator("#endpoint-rows tr")).toHaveCount(4);
  const hello = await request.get("/ex/hello");
  expect(await hello.text()).toBe('{"message":"Hello from MockAPI"}');
});

test("tests a configured non-2xx response and restores focus", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.getByRole("row", { name: /Rate limited response/ });
  const trigger = row.getByRole("button", { name: "Test" });
  await trigger.click();
  await page.getByLabel("Path and query").fill("/ex/rate-limited?browser=1");
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.getByRole("status")).toHaveText("Request completed with HTTP 429.");
  await expect(page.locator("#test-response-body")).toHaveText('{"error":"try again later"}');
  await expect(page.locator("#test-response-headers")).toContainText("x-mock-source: MockAPI, checked-in-example");
  await expectNoUnreviewedAccessibilityViolations(page);
  await page.keyboard.press("Escape");
  await expect(trigger).toBeFocused();
});

test("shows grounded endpoint statistics as a graph and table", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.getByRole("row", { name: /Rate limited response/ })).toBeVisible();
  const response = await request.get("/ex/rate-limited");
  expect(response.status()).toBe(429);

  await page.getByRole("button", { name: "By endpoint" }).click();
  await page.getByLabel("Endpoint statistics").selectOption({ label: "Rate limited response · /ex/rate-limited" });
  await expect(page.locator("#statistics-chart-title")).toHaveText("Rate limited response request activity");
  await expect(page.locator("#statistics-annotation")).toContainText(
    "Every matched request is configured to return HTTP 429."
  );
  await expect(page.locator("#statistics-annotation")).toContainText("configured Retry-After value is 30");
  await expect(page.locator("#statistics-annotation")).toContainText(
    "No request-count or time-window rate-limit condition is configured."
  );

  await page.getByRole("button", { name: "Table", exact: true }).click();
  const statisticsRow = page.locator("#statistics-table-view tbody tr").filter({ hasText: "Rate limited response" });
  await expect(statisticsRow).toContainText("1");
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("tests body-bearing and empty-response endpoints", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const createRow = page.getByRole("row", { name: /Create an order/ });
  await createRow.getByRole("button", { name: "Test" }).click();
  await page.getByLabel("Request body", { exact: true }).fill('{"sku":"browser"}');
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.getByRole("status")).toHaveText("Request completed with HTTP 201.");
  await expect(page.locator("#test-response-body")).toHaveText('{"id":42,"status":"created"}');
  await page.keyboard.press("Escape");

  const deleteRow = page.getByRole("row", { name: /Delete an order/ });
  await deleteRow.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.getByRole("status")).toHaveText("Request completed with HTTP 204.");
  await expect(page.locator("#test-response-body")).toHaveText("Empty response body.");
});

test("reports endpoint-test network errors", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await page.route("**/ex/hello", (route) => route.abort());
  const row = page.getByRole("row", { name: /Hello from MockAPI/ });
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.getByRole("status")).toContainText("Network error:");
});

test("cancels an active endpoint test", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await page.route("**/ex/hello", async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 2_000));
    await route.abort();
  });
  const row = page.getByRole("row", { name: /Hello from MockAPI/ });
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await page.getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByRole("status")).toHaveText("Request cancelled.");
});

test("reflows without page overflow", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth))
    .toBe(true);
  await page.getByRole("button", { name: "Test" }).first().click();
  const bladeWidth = await page.locator("#test-blade").evaluate((element) => element.getBoundingClientRect().width);
  const viewportWidth = await page.evaluate(() => document.documentElement.clientWidth);
  expect(bladeWidth).toBeLessThanOrEqual(viewportWidth + 1);
});

test("shows a disconnected state when management requests fail", async ({ page }) => {
  await page.route("**/__mockapi/api/**", (route) => route.abort());
  await page.reload();
  await expect(page.locator("#connection-status")).toContainText("Disconnected");
  await expect(page.locator("#persistence-label")).toHaveText("Management API unavailable");
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
