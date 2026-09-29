import { expect, test, expectNoUnreviewedAccessibilityViolations } from "./dashboard-fixtures.js";

test("positions and persists the endpoint test dialog alignment", async ({ page }) => {
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await page.getByRole("button", { name: "Settings" }).click();

  const settings = page.getByRole("dialog", { name: "Settings" });
  const alignment = settings.getByLabel("Endpoint test dialog position");
  await expect(alignment).toHaveValue("right");
  await alignment.selectOption("left");
  await settings.getByRole("button", { name: "Done" }).click();

  await page.getByRole("button", { name: "Test" }).first().click();
  const testDialog = page.getByRole("dialog", { name: /.+/ }).filter({ has: page.locator("#test-method") });
  let bounds = await testDialog.boundingBox();
  expect(bounds.x).toBe(0);
  expect(bounds.y).toBe(0);
  expect(bounds.height).toBe(900);
  await page.getByRole("button", { name: "Close endpoint test" }).last().click();

  await page.getByRole("button", { name: "Settings" }).click();
  await alignment.selectOption("center");
  await settings.getByRole("button", { name: "Done" }).click();
  await page.getByRole("button", { name: "Test" }).first().click();
  bounds = await testDialog.boundingBox();
  expect(Math.abs(bounds.x + bounds.width / 2 - 600)).toBeLessThanOrEqual(1);
  expect(bounds.y).toBe(24);
  expect(bounds.height).toBe(852);
  await page.getByRole("button", { name: "Close endpoint test" }).last().click();

  await page.reload();
  await page.getByRole("button", { name: "Test" }).first().click();
  bounds = await testDialog.boundingBox();
  expect(Math.abs(bounds.x + bounds.width / 2 - 600)).toBeLessThanOrEqual(1);
  expect(bounds.y).toBe(24);
  await expect
    .poll(() =>
      page.evaluate(() => JSON.parse(window.localStorage.getItem("mockapi.preferences")).endpointTestDialogAlignment)
    )
    .toBe("center");

  await page.getByRole("button", { name: "Close endpoint test" }).last().click();
  await page.getByRole("button", { name: "Settings" }).click();
  await alignment.selectOption("right");
  await settings.getByRole("button", { name: "Done" }).click();
  await page.getByRole("button", { name: "Test" }).first().click();
  bounds = await testDialog.boundingBox();
  expect(bounds.x + bounds.width).toBe(1200);
  expect(bounds.y).toBe(0);
  expect(bounds.height).toBe(900);
});

test("tests a configured non-2xx response and restores focus", async ({ page, request }) => {
  const exampleResponse = await request.get("/__mockapi/api/configuration/example");
  const example = await exampleResponse.json();
  expect(example.endpoints.find((endpoint) => endpoint.path === "/ex/rate-limited").requestCount).toBe(5);
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.getByRole("row", { name: /Rate limited response/ });
  const trigger = row.getByRole("button", { name: "Test" });
  await trigger.click();
  await page.getByLabel("Path and query").fill("/ex/rate-limited?browser=1");
  const requestCount = page.getByLabel("Number of requests");
  await expect(requestCount).toHaveValue("5");
  await expect(requestCount).toHaveAttribute("min", "1");
  await expect(requestCount).toHaveAttribute("max", "5");
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.getByRole("status")).toHaveText("5 requests completed; final response HTTP 429.");
  await expect(page.locator("#test-response-body")).toHaveText('{"error":"try again later"}');
  await expect(page.locator("#test-response-headers")).toContainText("x-mock-source");
  const colonColumns = await page.locator("#test-response-headers").evaluate((element) =>
    element.textContent
      .split("\n")
      .filter(Boolean)
      .map((line) => line.indexOf(":"))
  );
  expect(new Set(colonColumns).size).toBe(1);
  await expectNoUnreviewedAccessibilityViolations(page);
  await page.keyboard.press("Escape");
  await expect(trigger).toBeFocused();
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

test("reports endpoint-test network errors", async ({ page, request }) => {
  await request.post("/__mockapi/api/statistics/reset");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.locator("#endpoint-rows").getByRole("row", { name: /Abort the connection/ });
  await expect(row).toContainText("DROP");
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.getByRole("status")).toContainText("Network error:");
  const requestLog = page.getByRole("region", { name: "Recent request log" });
  const loggedRow = requestLog.getByRole("row", { name: /Abort the connection/ });
  await expect(loggedRow).toHaveCount(1);
  await expect
    .poll(async () => Number(await loggedRow.locator(".request-log-attempts").textContent()))
    .toBeGreaterThan(1);
  await expect(page.locator("#request-log-insight")).toContainText(
    "Chrome may automatically retry an idempotent request"
  );
  const statisticsResponse = await request.get("/__mockapi/api/statistics");
  expect(statisticsResponse.ok()).toBe(true);
  const statistics = await statisticsResponse.json();
  expect(statistics.totalRequests).toBe(1);
  expect(statistics.matchedRequests).toBe(1);
  expect(statistics.abortedConnections).toBe(1);
  await expect(loggedRow.locator("td").nth(1)).toHaveCSS("display", "table-cell");
  await expectNoUnreviewedAccessibilityViolations(page);
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
