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
  expect(
    example.endpoints.find((endpoint) => endpoint.path === "/ctp/attractions/cloud-cruiser/wait-times").requestCount
  ).toBe(5);
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.getByRole("row", { name: /Check Cloud Cruiser wait times/ });
  const trigger = row.getByRole("button", { name: "Test" });
  await trigger.click();
  await page.getByLabel("Path and query").fill("/ctp/attractions/cloud-cruiser/wait-times?browser=1");
  const requestCount = page.getByLabel("Number of requests");
  await expect(requestCount).toHaveValue("5");
  await expect(requestCount).toHaveAttribute("min", "1");
  await expect(requestCount).toHaveAttribute("max", "5");
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("5 requests completed; final response HTTP 429.");
  await expect(page.locator("#test-response-body")).toHaveText(
    '{"error":"wait_times_rate_limited","message":"Take a little breather! Check back in 10 seconds."}'
  );
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
  const createRow = page.getByRole("row", { name: /Reserve a ride/ });
  await createRow.getByRole("button", { name: "Test" }).click();
  await page.getByLabel("Request body", { exact: true }).fill('{"attractionId":"cloud-cruiser","partySize":2}');
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 201.");
  await expect(page.locator("#test-response-body")).toHaveText(
    '{"id":42,"parkId":"starlight-gardens","attractionId":"cloud-cruiser","partySize":2,"status":"reserved"}'
  );
  await page.keyboard.press("Escape");

  const deleteRow = page.getByRole("row", { name: /Cancel your ride reservation/ });
  await deleteRow.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 204.");
  await expect(page.locator("#test-response-body")).toHaveText("Empty response body.");
});

for (const { name, path, status, contentType } of [
  {
    name: "Reservation validation fault demo",
    path: "/ctp/demo-faults/reservation-validation",
    status: 422,
    contentType: "application/problem+json; charset=utf-8",
  },
  {
    name: "Reservation conflict fault demo",
    path: "/ctp/demo-faults/reservation-conflict",
    status: 409,
    contentType: "application/problem+json; charset=utf-8",
  },
  {
    name: "Ticket service maintenance fault demo",
    path: "/ctp/demo-faults/ticket-service",
    status: 503,
    contentType: "application/problem+json; charset=utf-8",
  },
  {
    name: "Download the park schedule",
    path: "/ctp/parks/starlight-gardens/schedule.csv",
    status: 200,
    contentType: "text/csv; charset=utf-8",
  },
  {
    name: "Update your ride reservation",
    path: "/ctp/reservations/42",
    status: 200,
    contentType: "application/json; charset=utf-8",
  },
  {
    name: "Check reservation preflight",
    path: "/ctp/reservations/42",
    status: 204,
    contentType: null,
  },
  {
    name: "Check the park API health",
    path: "/ctp/health",
    status: 200,
    contentType: null,
  },
]) {
  test(`previews and tests the built-in ${name} example`, async ({ page, request }) => {
    const example = await (await request.get("/__mockapi/api/configuration/example")).json();
    const endpoint = example.endpoints.find((candidate) => candidate.path === path && candidate.name === name);
    await page.getByRole("button", { name: "Load examples" }).first().click();
    const row = page.getByRole("row", { name: new RegExp(name) });
    await expect(row.locator(".status-badge")).toHaveText(String(status));
    await expect(row.locator(".method-badge")).toHaveText(endpoint.methods);
    await row.getByRole("button", { name: `Endpoint information for ${name}`, exact: true }).hover();
    await expect(row.getByText(endpoint.description, { exact: true })).toBeVisible();
    await row.getByRole("button", { name: "Test", exact: true }).click();
    await expect(page.getByLabel("Path and query")).toHaveValue(path);
    await expect(page.locator("#test-method")).toHaveValue(endpoint.methods[0]);
    if (endpoint.methods.includes("POST") || endpoint.methods.includes("PATCH")) {
      await page.getByLabel("Request body", { exact: true }).fill('{"partySize":2}');
    }
    await page.getByRole("button", { name: "Send request" }).click();
    await expect(page.locator("#test-status")).toHaveText(`Request completed with HTTP ${status}.`);
    await expect(page.locator("#test-response-body")).toHaveText(endpoint.response.body || "Empty response body.");
    if (contentType) {
      await expect(page.locator("#test-response-headers")).toContainText(contentType);
    }
    if (status === 503) {
      await expect(page.locator("#test-response-headers")).toContainText(/retry-after\s*: 30/);
    }
    if (contentType?.startsWith("text/csv")) {
      await expect(page.locator("#test-response-headers")).toContainText(
        'attachment; filename="starlight-gardens-schedule.csv"'
      );
    }
    if (endpoint.methods.includes("PATCH")) {
      expect((await (await request.get(path)).json()).partySize).toBe(2);
    }
    if (endpoint.methods.includes("OPTIONS")) {
      await expect(page.locator("#test-response-headers")).toContainText(/allow\s*: GET, PATCH, DELETE, OPTIONS/);
      await expect(page.locator("#test-response-headers")).toContainText(
        /access-control-allow-methods\s*: PATCH, OPTIONS/
      );
      await expect(page.locator("#test-response-headers")).not.toContainText(/(?:^|\n)content-type\s*:/);
    }
    if (endpoint.methods.includes("HEAD")) {
      await expect(page.locator("#test-response-headers")).toContainText(/x-mock-source\s*: MockAPI/);
      await expect(page.locator("#test-response-headers")).not.toContainText(/(?:^|\n)content-type\s*:/);
      expect((await request.get(path)).status()).toBe(404);
      expect((await request.head("/ctp/parks")).status()).toBe(404);
      await expect(page.getByRole("row", { name: /Discover the parks/ }).locator(".method-badge")).toHaveText(["GET"]);
    }
  });
}

test("performs a cross-origin PATCH preflight with API-key protection disabled", async ({ page, request, baseURL }) => {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(example.endpoints.length);
  const clientUrl = new URL(baseURL);
  clientUrl.hostname = "localhost";
  await page.goto(clientUrl.href);
  await expect(page.locator("#connection-status")).toHaveText("Live");

  const result = await page.evaluate(async (url) => {
    const response = await fetch(`${url}/ctp/reservations/42`, {
      method: "PATCH",
      credentials: "omit",
      headers: {
        "Content-Type": "application/json",
        "X-MockAPI-Key": "sample-key-for-cors-demo",
      },
      body: '{"partySize":6}',
    });
    return {
      status: response.status,
      source: response.headers.get("X-Mock-Source"),
      body: await response.json(),
    };
  }, baseURL);
  expect(result.status).toBe(200);
  expect(result.source).toBe("MockAPI");
  expect(result.body.partySize).toBe(4);
  expect((await (await request.get("/ctp/reservations/42")).json()).partySize).toBe(2);
  const options = example.endpoints.find((endpoint) => endpoint.methods.includes("OPTIONS"));
  await expect
    .poll(async () => {
      const statistics = await (await request.get("/__mockapi/api/statistics")).json();
      return statistics.endpoints.find((endpoint) => endpoint.endpointId === options.id)?.totalRequests;
    })
    .toBe(1);
});

test("reports endpoint-test network errors", async ({ page, request }) => {
  await request.post("/__mockapi/api/statistics/reset");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.locator("#endpoint-rows").getByRole("row", { name: /Ride sensor connection fault demo/ });
  await expect(row).toContainText("DROP");
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toContainText("Network error:");
  const requestLog = page.getByRole("region", { name: "Recent request log" });
  const loggedRow = requestLog.getByRole("row", { name: /Ride sensor connection fault demo/ });
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
  await page.route("**/ctp/parks", async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 2_000));
    await route.abort();
  });
  const row = page.getByRole("row", { name: /Discover the parks/ });
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.locator("#test-status")).toHaveText("Request cancelled.");
});
