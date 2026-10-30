import { expect, test, expectNoUnreviewedAccessibilityViolations, importDocument } from "./dashboard-fixtures.js";

test("positions and persists the endpoint test dialog alignment", async ({ page }) => {
  await page.setViewportSize({ width: 1200, height: 1400 });
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
  expect(bounds.height).toBeLessThan(1400);
  await page.getByRole("button", { name: "Close endpoint test" }).last().click();

  await page.getByRole("button", { name: "Settings" }).click();
  await alignment.selectOption("center");
  await settings.getByRole("button", { name: "Done" }).click();
  await page.getByRole("button", { name: "Test" }).first().click();
  bounds = await testDialog.boundingBox();
  expect(Math.abs(bounds.x + bounds.width / 2 - 600)).toBeLessThanOrEqual(1);
  expect(bounds.y).toBe(24);
  expect(bounds.height).toBeLessThan(1352);
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
  expect(bounds.height).toBeLessThan(1400);
});

test("tests a configured non-2xx response and restores focus", async ({ page, request }) => {
  const exampleResponse = await request.get("/__mockapi/api/configuration/example");
  const example = await exampleResponse.json();
  expect(
    example.endpoints.find((endpoint) => endpoint.path === "/shipping/demo-faults/tracking-rate-limit").requestCount
  ).toBe(3);
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.getByRole("row", { name: /Tracking rate-limit fault demo/ });
  const trigger = row.getByRole("button", { name: "Test" });
  await trigger.click();
  await page.getByLabel("Path and query").fill("/shipping/demo-faults/tracking-rate-limit?browser=1");
  const requestCount = page.getByLabel("Number of requests");
  await expect(requestCount).toHaveValue("3");
  await expect(requestCount).toHaveAttribute("min", "1");
  await expect(requestCount).toHaveAttribute("max", "5");
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("3 requests completed; final response HTTP 429.");
  await expect(page.locator("#test-response-body")).toHaveText(
    '{"type":"about:blank","title":"Too Many Requests","status":429,"detail":"The sample tracking request limit was exceeded. Retry in 10 seconds."}'
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

test("aligns request information icons and shares hover and focus tooltip behavior", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await page
    .getByRole("row", { name: /Reserve inventory/ })
    .getByRole("button", { name: "Test" })
    .click();

  const layout = await page.evaluate(() => {
    const headings = [...document.querySelectorAll(".test-request-field-heading")];
    const appearance = (element) => {
      const style = getComputedStyle(element);
      return [style.backgroundColor, style.color, style.width, style.height, style.borderRadius, style.fontSize];
    };
    return {
      reference: appearance(document.querySelector(".api-description-button")),
      payloadTypography: [
        "#test-request-headers",
        "#test-request-body",
        "#test-response-headers",
        "#test-response-body",
      ].map((selector) => {
        const style = getComputedStyle(document.querySelector(selector));
        return { fontSize: style.fontSize, fontFamily: style.fontFamily, lineHeight: style.lineHeight };
      }),
      tooltipStyles: [...document.querySelectorAll(".api-description-tooltip")].map((element) => {
        const style = getComputedStyle(element);
        return [style.backgroundColor, style.color, style.padding, style.fontSize, style.borderRadius, style.boxShadow];
      }),
      tooltipHelpTextWhitespace: [...document.querySelectorAll(".api-description-tooltip .test-field-help-text")].map(
        (element) => getComputedStyle(element).whiteSpace
      ),
      fields: headings.map((heading) => {
        const icon = heading.querySelector("button");
        const iconBounds = icon.getBoundingClientRect();
        const labelBounds = heading.querySelector("label").getBoundingClientRect();
        return {
          appearance: appearance(icon),
          x: iconBounds.x,
          centerDifference: Math.abs(iconBounds.y + iconBounds.height / 2 - labelBounds.y - labelBounds.height / 2),
        };
      }),
      headerGap:
        headings[0].getBoundingClientRect().top -
        document.querySelector(".request-target").getBoundingClientRect().bottom,
      bodyGap:
        headings[1].getBoundingClientRect().top -
        document.querySelector("#test-request-headers").getBoundingClientRect().bottom,
    };
  });
  for (const field of layout.fields) {
    expect(field.appearance).toEqual(layout.reference);
    expect(field.centerDifference).toBeLessThanOrEqual(1);
  }
  expect(layout.fields[0].x).toBe(layout.fields[1].x);
  expect(layout.headerGap).toBeGreaterThanOrEqual(18);
  expect(layout.bodyGap).toBeGreaterThanOrEqual(18);
  for (const style of layout.tooltipStyles) expect(style).toEqual(layout.tooltipStyles[0]);
  expect(layout.tooltipHelpTextWhitespace).toEqual(["normal", "normal"]);
  for (const typography of layout.payloadTypography) {
    expect(typography.fontSize).toBe("13px");
    expect(typography).toEqual(layout.payloadTypography[0]);
  }

  const guidance = page.locator("#test-request-headers-hint");
  await expect(guidance).toBeHidden();
  await page.getByRole("button", { name: "Request header information" }).hover();
  await expect(guidance).toContainText("One name: value pair per line.");
  await expect(guidance).toBeVisible();
  const bounds = await guidance.boundingBox();
  expect(bounds.x).toBeGreaterThanOrEqual(0);
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(page.viewportSize().width);
  await page.keyboard.press("Escape");
  await expect(guidance).toBeHidden();

  const headerInfo = page.getByRole("button", { name: "Request header information" });
  await page.locator("#test-request-headers").hover();
  await headerInfo.hover();
  await expect(guidance).toBeVisible();
  await page.locator("#test-request-headers").hover();
  await expect(guidance).toBeHidden();
  await page.locator("#test-method").focus();
  await headerInfo.focus();
  await expect(guidance).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(guidance).toBeHidden();
  await expect(page.locator("#test-blade")).toBeVisible();
  await page.getByRole("button", { name: "Request body information" }).focus();
  await expect(page.locator("#test-request-body-hint")).toBeVisible();
  await page.locator("#test-blade-close").click();
  await expect(page.locator("#test-request-body-hint")).toBeHidden();
  await page
    .getByRole("row", { name: /Reserve inventory/ })
    .getByRole("button", { name: "Test" })
    .click();
  await expect(guidance).toBeHidden();
  await expect(page.locator("#test-request-body-hint")).toBeHidden();

  const headers = page.locator("#test-request-headers");
  await expect(headers).toHaveAttribute("wrap", "off");
  const headerLines = "Content-Type: application/json; charset=utf-8\nX-Correlation-Id: sample-request";
  await headers.fill(headerLines);
  await expect(headers).toHaveValue(headerLines);
});

test("prefills an editable sample and displays real backend payload errors", async ({ page, request }) => {
  await request.post("/__mockapi/api/statistics/reset");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const createRow = page.getByRole("row", { name: /Reserve inventory/ });
  await createRow.getByRole("button", { name: "Test" }).click();

  const headers = page.locator("#test-request-headers");
  const body = page.locator("#test-request-body");
  await expect(headers).toHaveValue("Content-Type: application/json; charset=utf-8");
  expect(JSON.parse(await body.inputValue())).toEqual({
    sku: "SKU-1001",
    quantity: 2,
    warehouseId: "WH-DEMO-01",
  });
  await page.getByRole("button", { name: "Request header information" }).click();
  await expect(page.getByText("The browser requires valid header syntax", { exact: false })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByText("The browser requires valid header syntax", { exact: false })).toBeHidden();
  await expect(page.getByRole("button", { name: "Request header information" })).toBeFocused();
  await page.getByRole("button", { name: "Request body information" }).click();
  await expect(page.getByText("Empty payloads use the configured response", { exact: false })).toBeVisible();
  await expectNoUnreviewedAccessibilityViolations(page, "#test-blade");

  for (const [payload, expectedStatus] of [
    ["", 201],
    ["{invalid", 400],
  ]) {
    await body.fill(payload);
    const responsePromise = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === "/inventory/reservations" && response.request().method() === "POST"
    );
    await page.getByRole("button", { name: "Send request" }).click();
    const response = await responsePromise;
    expect(response.request().postData() ?? "").toBe(payload);
    expect(response.status()).toBe(expectedStatus);
    if (expectedStatus === 400) {
      expect((await response.json()).status).toBe(400);
      await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 400.");
      await expect(page.locator("#test-response-body")).toContainText('"status":400');
    } else {
      await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 201.");
      await expect(page.locator("#test-response-body")).toContainText('"id":"res-demo-001"');
    }
  }

  await body.fill('{"unexpectedField":"business content is not validated"}');
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 201.");
  const statistics = await (await request.get("/__mockapi/api/statistics")).json();
  expect(statistics.totalRequests).toBe(3);
});

test("prefills POST PUT and PATCH without replacing deliberate request edits", async ({ page, request }) => {
  await importDocument(request, {
    schemaVersion: "1.0",
    endpoints: [
      {
        id: "a0a75250-c57b-48c3-a809-95300372c07e",
        name: "Payload methods",
        enabled: true,
        methods: ["GET", "POST", "PUT", "PATCH"],
        path: "/payload-methods",
        response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "configured" },
      },
    ],
  });
  await page.getByRole("button", { name: "Test", exact: true }).click();
  const method = page.locator("#test-method");
  const body = page.locator("#test-request-body");
  const headers = page.locator("#test-request-headers");
  await expect(body).toHaveValue("");
  for (const selection of ["POST", "PUT", "PATCH"]) {
    await method.selectOption(selection);
    expect(JSON.parse(await body.inputValue())).toEqual({
      id: "sample-item-001",
      name: "Example item",
      status: "active",
      quantity: 2,
      tags: ["sample", "dashboard"],
      metadata: { source: "MockAPI dashboard" },
    });
    await expect(headers).toHaveValue("Content-Type: application/json; charset=utf-8");
  }
  await body.fill("");
  await headers.fill("Content-Type: application/problem+json");
  await method.selectOption("PUT");
  await expect(body).toHaveValue("");
  await expect(headers).toHaveValue("Content-Type: application/problem+json");
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 200.");
  await body.fill("{deliberately-malformed");
  await method.selectOption("PATCH");
  await expect(body).toHaveValue("{deliberately-malformed");
});

test("tests body-bearing and empty-response endpoints", async ({ page, request }) => {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  const reservationBody = example.endpoints.find((endpoint) => endpoint.path === "/inventory/reservations").response
    .body;
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const createRow = page.getByRole("row", { name: /Reserve inventory/ });
  await createRow.getByRole("button", { name: "Test" }).click();
  await page.getByLabel("Request headers", { exact: true }).fill("Content-Type: application/json; charset=utf-8");
  await page.getByLabel("Request body", { exact: true }).fill('{"sku":"SKU-1001","quantity":2}');
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 201.");
  await expect(page.locator("#test-response-body")).toHaveText(reservationBody);
  const prettyPrint = page.getByRole("button", { name: "Pretty print" });
  await expect(prettyPrint).toBeVisible();
  await prettyPrint.click();
  await expect(page.locator("#test-response-body-label")).toHaveText("Pretty response body");
  await expect(page.locator("#test-response-body")).toHaveText(JSON.stringify(JSON.parse(reservationBody), null, 2));
  await page.getByRole("button", { name: "Show raw" }).click();
  await expect(page.locator("#test-response-body-label")).toHaveText("Raw response body");
  await expect(page.locator("#test-response-body")).toHaveText(reservationBody);
  await page.keyboard.press("Escape");

  const deleteRow = page.getByRole("row", { name: /Cancel the sample reservation/ });
  await deleteRow.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toHaveText("Request completed with HTTP 204.");
  await expect(page.locator("#test-response-body")).toHaveText("Empty response body.");
  await expect(page.getByRole("button", { name: "Pretty print" })).toBeHidden();
});

test("prefers and persists pretty endpoint test responses when available", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await page.getByRole("button", { name: "Settings" }).click();
  const settings = page.getByRole("dialog", { name: "Settings" });
  const responseBodyDisplay = settings.getByLabel("Response body display");
  await expect(responseBodyDisplay).toHaveValue("raw");
  await responseBodyDisplay.selectOption("pretty");
  await settings.getByRole("button", { name: "Done" }).click();

  const row = page.getByRole("row", { name: /Reserve inventory/ });
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-response-body-label")).toHaveText("Pretty response body");
  await expect(page.locator("#test-response-body")).toContainText('\n  "id": "res-demo-001",');
  await expect(page.getByRole("button", { name: "Show raw" })).toBeVisible();
  await page.getByRole("button", { name: "Close endpoint test" }).last().click();

  await page.reload();
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-response-body-label")).toHaveText("Pretty response body");
  await expect
    .poll(() => page.evaluate(() => JSON.parse(localStorage.getItem("mockapi.preferences")).responseBodyPresentation))
    .toBe("pretty");
});

test("keeps request and response sections compact and closes from the backdrop", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const trigger = page.getByRole("row", { name: /Browse inventory/ }).getByRole("button", { name: "Test" });
  await trigger.click();

  const requestActions = await page.locator(".test-actions").boundingBox();
  const responseSection = await page.locator(".test-response").boundingBox();
  expect(responseSection.y - (requestActions.y + requestActions.height)).toBeLessThanOrEqual(20);

  if (page.viewportSize().width > 640) {
    await page.locator("#test-blade-backdrop").click({ position: { x: 10, y: 10 } });
  } else {
    await page.getByRole("button", { name: "Close endpoint test" }).last().click();
  }
  await expect(page.locator("#test-blade-shell")).toBeHidden();
  await expect(trigger).toBeFocused();
});

for (const { name, path, status, contentType, method } of [
  {
    name: "Trailhead assistant - success",
    path: "/aoai/200/chat/completions",
    status: 200,
    contentType: "application/json; charset=utf-8",
  },
  {
    name: "Reservation validation fault demo",
    path: "/inventory/demo-faults/invalid-reservation",
    status: 422,
    contentType: "application/problem+json; charset=utf-8",
  },
  {
    name: "Insufficient stock fault demo",
    path: "/inventory/demo-faults/insufficient-stock",
    status: 409,
    contentType: "application/problem+json; charset=utf-8",
  },
  {
    name: "Warehouse outage fault demo",
    path: "/inventory/demo-faults/warehouse-unavailable",
    status: 503,
    contentType: "application/problem+json; charset=utf-8",
  },
  {
    name: "Download a demonstration label",
    path: "/shipping/shipments/shp-demo-001/label.txt",
    status: 200,
    contentType: "text/plain; charset=utf-8",
  },
  {
    name: "Create a sample shipment",
    path: "/shipping/shipments",
    status: 201,
    contentType: "application/json; charset=utf-8",
  },
  {
    name: "Cancel the sample reservation",
    path: "/inventory/reservations/res-demo-001",
    status: 204,
    contentType: null,
  },
  {
    name: "Inspect an in-stock product",
    path: "/inventory/items/SKU-1001",
    status: 200,
    contentType: "application/json; charset=utf-8",
  },
  {
    name: "Inspect the sample shipment",
    path: "/shipping/shipments/shp-demo-001",
    status: 200,
    contentType: "application/json; charset=utf-8",
  },
  {
    name: "Trailhead assistant - health",
    path: "/aoai/health",
    status: 200,
    contentType: null,
    method: "HEAD",
  },
  {
    name: "Inventory API health",
    path: "/inventory/health",
    status: 200,
    contentType: null,
    method: "HEAD",
  },
  {
    name: "Shipping API health",
    path: "/shipping/health",
    status: 200,
    contentType: null,
    method: "HEAD",
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
    if (method) await page.locator("#test-method").selectOption(method);
    if (endpoint.methods.includes("POST") || endpoint.methods.includes("PATCH")) {
      await page.getByLabel("Request body", { exact: true }).fill('{"quantity":2}');
    }
    await page.getByRole("button", { name: "Send request" }).click();
    await expect(page.locator("#test-status")).toHaveText(`Request completed with HTTP ${status}.`);
    await expect(page.locator("#test-response-body")).toHaveText(
      method === "HEAD" ? "Empty response body." : endpoint.response.body || "Empty response body."
    );
    if (contentType) {
      await expect(page.locator("#test-response-headers")).toContainText(contentType);
    }
    if (status === 503) {
      await expect(page.locator("#test-response-headers")).toContainText(/retry-after\s*: 30/);
    }
    if (contentType?.startsWith("text/plain")) {
      await expect(page.locator("#test-response-headers")).toContainText(
        "attachment; filename=shipping-label-demo-001.txt"
      );
    }
    if (endpoint.methods.includes("HEAD")) {
      await expect(page.locator("#test-response-headers")).toContainText(/x-mock-source\s*: MockAPI/);
      expect((await request.get(path)).status()).toBe(404);
    } else if (name.startsWith("Inspect")) {
      expect((await request.head(path)).status()).toBe(404);
      await expect(row.locator(".method-badge")).toHaveText(["GET"]);
    }
  });
}

test("performs a cross-origin PATCH preflight with API-key protection disabled", async ({ page, request, baseURL }) => {
  const corsHeaders = {
    "Access-Control-Allow-Origin": ["*"],
    "Access-Control-Allow-Methods": ["PATCH, OPTIONS"],
    "Access-Control-Allow-Headers": ["Content-Type, X-MockAPI-Key"],
    "Access-Control-Expose-Headers": ["X-Mock-Source"],
    "X-Mock-Source": ["MockAPI"],
  };
  const preflight = {
    id: "c4880e93-664a-4c8f-8915-bdd01f629311",
    name: "CORS preflight",
    enabled: true,
    methods: ["OPTIONS"],
    path: "/cors/item",
    response: { statusCode: 204, headers: corsHeaders, body: "" },
  };
  await importDocument(request, {
    schemaVersion: "1.0",
    endpoints: [
      preflight,
      {
        id: "9ebf089a-cc5a-46c6-9e8e-3a953204f9ab",
        name: "CORS update",
        enabled: true,
        methods: ["PATCH"],
        path: "/cors/item",
        response: {
          statusCode: 200,
          headers: corsHeaders,
          contentType: "application/json",
          body: '{"status":"updated"}',
        },
      },
      {
        id: "46cfac48-20c3-40d9-99cc-c27e2bc020f0",
        name: "CORS lookup",
        enabled: true,
        methods: ["GET"],
        path: "/cors/item",
        response: { statusCode: 200, headers: {}, contentType: "application/json", body: '{"status":"original"}' },
      },
    ],
  });
  const clientUrl = new URL(baseURL);
  clientUrl.hostname = "localhost";
  await page.goto(clientUrl.href);
  await expect(page.locator("#connection-status")).toHaveText("Live");

  const result = await page.evaluate(async (url) => {
    const response = await fetch(`${url}/cors/item`, {
      method: "PATCH",
      credentials: "omit",
      headers: {
        "Content-Type": "application/json",
        "X-MockAPI-Key": "sample-key-for-cors-demo",
      },
      body: '{"status":"requested"}',
    });
    return {
      status: response.status,
      source: response.headers.get("X-Mock-Source"),
      body: await response.json(),
    };
  }, baseURL);
  expect(result.status).toBe(200);
  expect(result.source).toBe("MockAPI");
  expect(result.body).toEqual({ status: "updated" });
  expect(await (await request.get("/cors/item")).json()).toEqual({ status: "original" });
  await expect
    .poll(async () => {
      const statistics = await (await request.get("/__mockapi/api/statistics")).json();
      return statistics.endpoints.find((endpoint) => endpoint.endpointId === preflight.id)?.totalRequests;
    })
    .toBe(1);
});

test("reports endpoint-test network errors", async ({ page, request }) => {
  await request.post("/__mockapi/api/statistics/reset");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const row = page.locator("#endpoint-rows").getByRole("row", { name: /Carrier connection fault demo/ });
  await expect(row).toContainText("DROP");
  await row.getByRole("button", { name: "Test" }).click();
  await page.getByRole("button", { name: "Send request" }).click();
  await expect(page.locator("#test-status")).toContainText("Network error:");
  const requestLog = page.getByRole("region", { name: "Recent request log" });
  const loggedRow = requestLog
    .locator(".request-log-entry")
    .filter({ hasText: "/shipping/demo-faults/connection-failure" });
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
  let releaseRequest;
  const requestHeld = new Promise((resolve) => {
    releaseRequest = resolve;
  });
  await page.route("**/inventory/items", async (route) => {
    // Keep the request active regardless of how long mobile actionability checks take.
    await requestHeld;
    await route.abort();
  });
  try {
    const row = page.getByRole("row", { name: /Browse inventory/ });
    await row.getByRole("button", { name: "Test" }).click();
    const requestStarted = page.waitForRequest("**/inventory/items");
    const send = page.getByRole("button", { name: "Send request" });
    const cancel = page.getByRole("button", { name: "Cancel", exact: true });
    await send.click();
    await requestStarted;
    await expect(send).toBeDisabled();
    await cancel.click();
    await expect(page.locator("#test-status")).toHaveText("Request cancelled.");
    await expect(send).toBeEnabled();
    await expect(cancel).toBeDisabled();
  } finally {
    releaseRequest();
    await page.unrouteAll({ behavior: "wait" });
  }
});
