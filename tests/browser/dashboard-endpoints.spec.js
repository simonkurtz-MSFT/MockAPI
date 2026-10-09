import {
  expect,
  test,
  emptyDocument,
  importDocument,
  expectNoUnreviewedAccessibilityViolations,
  contrastRatio,
} from "./dashboard-fixtures.js";

test("displays each supported HTTP method in a separate bordered badge", async ({ page, request }) => {
  await importDocument(request, {
    ...emptyDocument,
    endpoints: [
      {
        id: "a02c91c0-2878-40fc-a312-2dd7148af993",
        name: "Separate method labels",
        enabled: true,
        methods: ["GET", "HEAD"],
        path: "/method-labels",
        response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
      },
    ],
  });
  const badges = page.getByRole("row", { name: /Separate method labels/ }).locator(".method-badge");
  await expect(badges).toHaveText(["GET", "HEAD"]);
  for (const theme of ["light", "dark"]) {
    await page.evaluate((value) => (document.documentElement.dataset.theme = value), theme);
    for (const badge of await badges.all()) {
      const style = await badge.evaluate((element) => {
        const computed = getComputedStyle(element);
        return {
          borderStyle: computed.borderTopStyle,
          borderWidth: computed.borderTopWidth,
          padding: computed.paddingLeft,
          foreground: computed.color,
          background: computed.backgroundColor,
        };
      });
      expect(style.borderStyle).toBe("solid");
      expect(style.borderWidth).toBe("1px");
      expect(style.padding).toBe("6px");
      expect(contrastRatio(style.foreground, style.background)).toBeGreaterThanOrEqual(4.5);
    }
  }
});

test("shows all operations with relative paths and sorts within alphabetical API groups", async ({ page, request }) => {
  const endpoints = Array.from({ length: 24 }, (_, index) => {
    const group = index < 12 ? "zeta" : "alpha";
    const number = 12 - (index % 12);
    return {
      id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
      name: `Operation ${String(number).padStart(2, "0")}`,
      enabled: true,
      methods: ["GET"],
      path: `/${group}/route-${String(13 - number).padStart(2, "0")}`,
      response: {
        statusCode: group === "alpha" ? 500 : 200,
        headers: {},
        contentType: "text/plain",
        body: "ok",
      },
    };
  });
  await importDocument(request, { ...emptyDocument, endpoints });
  const rows = page.locator("#endpoint-rows .endpoint-row");
  const groups = page.locator(".endpoint-group-toggle > strong");
  const ascendingNames = Array.from({ length: 12 }, (_, index) => `Operation ${String(index + 1).padStart(2, "0")}`);
  await expect(rows).toHaveCount(24);
  await expect(groups).toHaveText(["alpha/", "zeta/"]);
  await expect(rows.locator(".endpoint-name-heading > strong")).toHaveText([...ascendingNames, ...ascendingNames]);
  await expect(page.locator(".table-wrap thead th").nth(1)).toHaveText("Path");
  await expect(rows.first().locator("td").nth(1)).toHaveText("/route-12");
  await expect(rows.first().locator(".path-cell")).toHaveAttribute("title", "/alpha/route-12");
  expect((await request.get("/alpha/route-12")).status()).toBe(500);
  const stored = await (await request.get("/__mockapi/api/endpoints")).json();
  expect(stored.find((endpoint) => endpoint.id === endpoints[23].id).path).toBe("/alpha/route-12");

  await page.locator('[data-sort-key="name"]').click();
  await expect(groups).toHaveText(["alpha/", "zeta/"]);
  await expect(rows.locator(".endpoint-name-heading > strong")).toHaveText([
    ...ascendingNames.toReversed(),
    ...ascendingNames.toReversed(),
  ]);
  await page.locator('[data-sort-key="response"]').click();
  await expect(groups).toHaveText(["alpha/", "zeta/"]);
  await expect(rows.first().locator(".status-badge")).toHaveText("500");

  await page.getByRole("button", { name: "Collapse alpha/", exact: true }).click();
  await page.locator('[data-sort-key="path"]').click();
  await expect(rows).toHaveCount(12);
  await expect(groups).toHaveText(["alpha/", "zeta/"]);
  await expect(page.getByRole("button", { name: "Expand alpha/", exact: true })).toBeVisible();
  await expect(rows.locator(".path-cell")).toHaveText(
    Array.from({ length: 12 }, (_, index) => `/route-${String(index + 1).padStart(2, "0")}`)
  );
  await page.getByRole("button", { name: "Expand alpha/", exact: true }).click();
  await expect(rows).toHaveCount(24);
  await page.locator("#select-all-endpoints").check();
  await expect(page.locator("#selection-count")).toHaveText("24");
  await expect(rows.locator(".selection-checkbox:checked")).toHaveCount(24);
  await expect(page.getByRole("navigation", { name: "Endpoint pages" })).toHaveCount(0);

  const region = page.getByRole("region", { name: "Endpoint registry", exact: true });
  expect(await region.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);
  await region.evaluate((element) => (element.scrollTop = element.scrollHeight));
  const regionBounds = await region.boundingBox();
  const headerBounds = await page.locator(".table-wrap thead").boundingBox();
  expect(Math.abs(regionBounds.y - headerBounds.y)).toBeLessThanOrEqual(1);
});

test("@smoke loads examples idempotently and filters by status", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);
  await expect(page.getByRole("navigation", { name: "Endpoint pages" })).toHaveCount(0);
  const responseHeader = page.getByRole("columnheader", { name: "Response" });
  await responseHeader.getByRole("button").click();
  await responseHeader.getByRole("button").click();
  await expect(responseHeader).toHaveAttribute("aria-sort", "descending");
  await expect(page.locator("#endpoint-rows .endpoint-row").first().locator(".status-badge")).toHaveText("DROP");
  await page.getByRole("columnheader", { name: "Endpoint" }).getByRole("button").click();
  const exampleGroup = page.getByRole("button", { name: "Collapse ctp/" });
  await expect(exampleGroup).toContainText("9");
  await page.locator("#endpoint-rows .endpoint-row").first().locator(".selection-checkbox").check();
  await expect(page.locator("#selection-count")).toHaveText("1");
  await exampleGroup.click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(0);
  await expect(page.locator("#selection-count")).toHaveText("1");
  await page.locator("#select-all-endpoints").check();
  await expect(page.locator("#selection-count")).toHaveText("9");
  await page.getByRole("button", { name: "Expand ctp/" }).click();
  await expect(page.locator("#endpoint-rows .selection-checkbox:checked")).toHaveCount(9);
  await page.locator("#select-all-endpoints").uncheck();
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.getByText("No example changes were needed")).toBeVisible();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);

  await page.locator("#filter-status").selectOption("3");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(1);
  await expect(page.getByRole("row", { name: /Find the featured attraction/ }).locator(".status-badge")).toHaveText(
    "302"
  );
  const redirect = await request.get("/ctp/attractions/featured", { maxRedirects: 0 });
  expect(redirect.status()).toBe(302);
  expect(redirect.headers().location).toBe("/ctp/attractions/cloud-cruiser");

  await page.locator("#filter-status").selectOption("5");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(1);
  await expect(page.getByRole("row", { name: /Parade schedule fault demo/ }).locator(".status-badge")).toHaveText(
    "500"
  );
  const serverError = await request.get("/ctp/demo-faults/parade-schedule");
  expect(serverError.status()).toBe(500);
  expect(await serverError.json()).toEqual({
    error: "parade_schedule_unavailable",
    message: "The parade schedule hit a little hiccup. Please try again later.",
  });

  await page.locator("#filter-status").selectOption("4");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(1);
  const rateLimitedRow = page.locator("#endpoint-rows").getByRole("row", { name: /Check Cloud Cruiser wait times/ });
  await expect(rateLimitedRow.locator(".endpoint-name-heading > strong")).toHaveText("Check Cloud Cruiser wait times");
  const responseCode = rateLimitedRow.locator(".status-badge");
  await responseCode.hover();
  await expect(responseCode).toHaveAttribute("title", "Too Many Requests");
  await expectNoUnreviewedAccessibilityViolations(page, ".request-log-workspace");
});

test("creates, edits, disables, and deletes an endpoint", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await expect(page.getByLabel("Operation ID", { exact: true })).toBeHidden();
  await page.locator("#field-name").fill("Browser endpoint");
  await page.locator("#field-description").fill("Created through the dashboard");
  await page.locator("#field-path").fill("/browser-test");
  await page.locator("#field-body").fill('{"message":"created in browser"}');
  await page.locator("#field-body").blur();
  await expect(page.locator("#field-body")).toHaveValue('{\n  "message": "created in browser"\n}');
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  const row = page.getByRole("row", { name: /Browser endpoint/ });
  await expect(row).toBeVisible();
  await expect(row.getByText("Created through the dashboard", { exact: true })).toBeHidden();
  const informationButton = row.getByRole("button", {
    name: "Endpoint information for Browser endpoint",
    exact: true,
  });
  await informationButton.hover();
  await expect(row.getByText("Created through the dashboard", { exact: true })).toBeVisible();
  await expect(row.locator(".endpoint-id")).toHaveCount(0);
  await expect(row.locator(".api-description-detail")).toHaveText(/^Operation ID: [0-9a-f-]{36}$/);
  await expect(informationButton).toHaveCSS("display", "flex");
  await expect(informationButton).toHaveCSS("align-items", "center");
  await expect(informationButton).toHaveCSS("justify-content", "center");

  await row.getByRole("button", { name: "Edit" }).click();
  const operationId = await row.getAttribute("data-endpoint-id");
  await expect(page.getByLabel("Operation ID", { exact: true })).toHaveValue(operationId);
  await expect(page.getByLabel("Operation ID", { exact: true })).toHaveAttribute("readonly", "");
  await page.locator("#field-name").fill("Edited browser endpoint");
  await page.locator("#field-description").fill("Updated through the dashboard");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  const editedRow = page.getByRole("row", { name: /Edited browser endpoint/ });
  await expect(editedRow).toBeVisible();
  await expect(editedRow).toHaveAttribute("data-endpoint-id", operationId);
  await editedRow
    .getByRole("button", { name: "Endpoint information for Edited browser endpoint", exact: true })
    .hover();
  await expect(editedRow.getByText("Updated through the dashboard", { exact: true })).toBeVisible();
  const enabledToggle = editedRow.locator(".switch input");
  await enabledToggle.uncheck();
  await expect(editedRow.getByRole("checkbox", { name: "Enable Edited browser endpoint" })).not.toBeChecked();
  await expect(enabledToggle).toBeEnabled();
  await expect(enabledToggle).toBeFocused();
  await editedRow.getByRole("button", { name: "Delete" }).click();
  await page.getByRole("button", { name: "Confirm" }).click();
  await expect(editedRow).toHaveCount(0);
});

test("keeps column geometry stable through group collapse and sorting, with centered attempt tooltips", async ({
  page,
  request,
}) => {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  await importDocument(request, example);
  await expect(page.locator(".endpoint-row")).toHaveCount(9);
  for (const width of [1200, 1920]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", width === 1200 ? "stacked" : "columns");
    const headers = page.locator(".table-wrap thead th");
    await expect(headers).toHaveCount(8);
    const original = await headers.evaluateAll((cells) =>
      cells.map((cell) => ({ x: cell.getBoundingClientRect().x, width: cell.getBoundingClientRect().width }))
    );
    expect(original[2].width).toBeGreaterThan(original[4].width * 2);
    for (const [column, maximumWidth] of [
      [4, 80],
      [5, 80],
      [6, 72],
      [7, 160],
    ]) {
      expect(original[column].width).toBeLessThanOrEqual(maximumWidth + 1);
    }
    const row = page.locator(".endpoint-row").first();
    const information = await row.locator(".api-description-button").boundingBox();
    const name = await row.locator(".endpoint-name-heading > strong").boundingBox();
    expect(information.x + information.width).toBeLessThan(name.x);
    const actions = row.locator(".row-actions");
    const actionBounds = await actions.boundingBox();
    const buttons = await actions.locator("button").evaluateAll((elements) =>
      elements.map((element) => {
        const bounds = element.getBoundingClientRect();
        return { x: bounds.x, width: bounds.width };
      })
    );
    expect(buttons[0].x).toBeGreaterThanOrEqual(actionBounds.x);
    expect(buttons.at(-1).x + buttons.at(-1).width).toBeLessThanOrEqual(actionBounds.x + actionBounds.width);
    for (const button of buttons) expect(button.width).toBe(32);
    await page.getByRole("button", { name: "Collapse ctp/", exact: true }).click();
    await expect(page.locator(".endpoint-row")).toHaveCount(0);
    const collapsed = await headers.evaluateAll((cells) =>
      cells.map((cell) => ({ x: cell.getBoundingClientRect().x, width: cell.getBoundingClientRect().width }))
    );
    expect(collapsed).toEqual(original);
    await page.getByRole("button", { name: "Expand ctp/", exact: true }).click();
    await expect(page.locator(".endpoint-row")).toHaveCount(9);
    const expanded = await headers.evaluateAll((cells) =>
      cells.map((cell) => ({ x: cell.getBoundingClientRect().x, width: cell.getBoundingClientRect().width }))
    );
    // Clicking a sort button may scroll the table horizontally; compare column widths.
    expect(expanded.map((cell) => cell.width)).toEqual(original.map((cell) => cell.width));
    const attempts = page.locator("[data-endpoint-requests]").first();
    await expect(attempts).toHaveCSS("text-align", "center");
    await expect(attempts).toHaveAttribute("title", "Last attempt: Never");
    await expect(attempts).toHaveAttribute("aria-label", "0 attempts. Last attempt: Never");
    const headingCenter = await page.locator('[data-sort-key="requests"]').evaluate((button) => {
      const range = document.createRange();
      const textNode = button.firstChild;
      const start = textNode.textContent.indexOf("Attempts");
      range.setStart(textNode, start);
      range.setEnd(textNode, start + "Attempts".length);
      const text = range.getBoundingClientRect();
      const bounds = button.getBoundingClientRect();
      return Math.abs(text.x + text.width / 2 - (bounds.x + bounds.width / 2));
    });
    expect(headingCenter).toBeLessThanOrEqual(1);
    await page.locator('[data-sort-key="name"]').click();
  }
});

test("duplicate drafts do not display or reuse the source operation ID", async ({ page, request }) => {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  await importDocument(request, { ...example, endpoints: [example.endpoints[0]] });
  const source = page.locator(".endpoint-row");
  await expect(source).toHaveCount(1);
  const sourceId = await source.getAttribute("data-endpoint-id");
  await source.getByRole("button", { name: "Edit", exact: true }).click();
  await expect(page.getByLabel("Operation ID", { exact: true })).toHaveValue(sourceId);
  await page.keyboard.press("Escape");
  await source.getByRole("button", { name: "Duplicate", exact: true }).click();
  await expect(page.getByLabel("Operation ID", { exact: true })).toBeHidden();
  await expect(page.locator("#field-operation-id")).toHaveValue("");
  await page.getByRole("button", { name: "Apply endpoint", exact: true }).click();
  await expect(page.locator(".endpoint-row")).toHaveCount(2);
  const copy = page.locator(".endpoint-row").filter({ hasText: `${example.endpoints[0].name} copy` });
  await expect(copy).not.toHaveAttribute("data-endpoint-id", sourceId);
  await copy.getByRole("button", { name: "Edit", exact: true }).click();
  await expect(page.getByLabel("Operation ID", { exact: true })).toHaveValue(
    await copy.getAttribute("data-endpoint-id")
  );
});

test("selects multiple or all endpoints for bulk actions", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const rows = page.locator("#endpoint-rows .endpoint-row");
  await expect(rows).toHaveCount(9);

  await rows.nth(0).locator(".selection-checkbox").check();
  await rows.nth(1).locator(".selection-checkbox").check();
  await expect(page.locator("#selection-count")).toHaveText("2");
  await expectNoUnreviewedAccessibilityViolations(page);
  await page.getByRole("button", { name: "Disable" }).click();
  await expect(page.getByText("Disabled 2 endpoints", { exact: true })).toBeVisible();
  await expect(rows.nth(0).getByRole("checkbox", { name: /^Enable |^Disable / })).not.toBeChecked();
  await expect(rows.nth(1).getByRole("checkbox", { name: /^Enable |^Disable / })).not.toBeChecked();

  await page.locator("#select-all-endpoints").check();
  await expect(page.locator("#selection-count")).toHaveText("9");
  await page.getByRole("button", { name: "Enable", exact: true }).click();
  await expect(page.getByText("Enabled 9 endpoints", { exact: true })).toBeVisible();
  await expect(rows.getByRole("checkbox", { name: /^Disable / })).toHaveCount(9);

  await page.locator("#select-all-endpoints").check();
  await page.locator("#bulk-delete").click();
  await expect(page.getByRole("dialog", { name: "Delete 9 selected endpoints" })).toBeVisible();
  await page
    .getByRole("dialog", { name: "Delete 9 selected endpoints" })
    .getByRole("button", { name: "Delete", exact: true })
    .click();
  await expect(rows).toHaveCount(0);
});

test("offers standard and custom status codes with synchronized reason phrases", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  const status = page.locator("#field-status");
  await expect(status.locator("option")).toHaveCount(63);
  await expect(status.locator('option[value="429"]')).toHaveText("429 Too Many Requests");

  await status.selectOption("429");
  await expect(page.locator("#field-reason")).toHaveValue("Too Many Requests");
  await status.selectOption("other");
  await expect(page.locator("#field-status-other")).toBeVisible();
  await expect(page.locator("#field-reason")).toHaveValue("");
  await page.locator("#field-status-other").fill("599");
  await page.locator("#field-name").fill("Custom status endpoint");
  await page.locator("#field-path").fill("/custom-status");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.getByRole("row", { name: /Custom status endpoint/ })).toContainText("599");
});

test("color-codes response status families with accessible contrast, including hovered rows", async ({
  page,
  request,
}) => {
  const endpoints = [101, 200, 302, 404, 503].map((statusCode) => ({
    id: `${statusCode}00000-0000-4000-8000-000000000000`,
    name: `Status ${statusCode}`,
    enabled: true,
    methods: ["GET"],
    path: `/status-${statusCode}`,
    response: { statusCode, headers: {}, contentType: "text/plain", body: String(statusCode) },
  }));
  await importDocument(request, { ...emptyDocument, endpoints });

  for (const theme of ["light", "dark"]) {
    await page.goto(`/?scoutTheme=${theme}`);
    for (const statusCode of [101, 200, 302, 404, 503]) {
      const statusClass = Math.floor(statusCode / 100);
      const badge = page.getByRole("row", { name: new RegExp(`Status ${statusCode}`) }).locator(".status-badge");
      await expect(badge).toHaveClass(new RegExp(`status-${statusClass}xx`));
      const colors = await badge.evaluate((element) => {
        const colorProbe = document.createElement("span");
        colorProbe.style.backgroundColor = "var(--cp-surface)";
        document.body.append(colorProbe);
        const colors = {
          background: getComputedStyle(colorProbe).backgroundColor,
          foreground: getComputedStyle(element).color,
        };
        colorProbe.remove();
        return colors;
      });
      expect(contrastRatio(colors.foreground, colors.background)).toBeGreaterThanOrEqual(4.5);
    }
    await page.getByRole("row", { name: /Status 503/ }).hover();
    await expectNoUnreviewedAccessibilityViolations(page, "#endpoint-rows");
  }
});

test("configures a request-count and time-window rate limit", async ({ page, request }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await page.locator("#field-name").fill("Conditional rate limit");
  await page.locator("#field-path").fill("/conditional-rate-limit");
  await page.locator("#field-status").selectOption("429");
  await page.locator("#field-body").fill('{"error":"limited"}');
  await page.locator("#field-rate-limit-enabled").check();
  await expect(page.locator("#rate-limit-fields")).toBeVisible();
  await page.locator("#field-request-limit").fill("4");
  await page.locator("#field-window-seconds").fill("15");
  await page.locator("#field-success-status").fill("202");
  await page.locator("#field-success-body").fill('{"status":"accepted"}');
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.getByRole("row", { name: /Conditional rate limit/ })).toBeVisible();

  const endpointsResponse = await request.get("/__mockapi/api/endpoints");
  const endpoints = await endpointsResponse.json();
  const endpoint = endpoints.find((candidate) => candidate.path === "/conditional-rate-limit");
  expect(endpoint.response.statusCode).toBe(429);
  expect(endpoint.response.rateLimit).toEqual({
    requestLimit: 4,
    windowSeconds: 15,
    successResponse: {
      statusCode: 202,
      reasonPhrase: null,
      headers: {},
      contentType: "application/json; charset=utf-8",
      body: '{"status":"accepted"}',
    },
  });
});

test("checks JSON response bodies and prevents applying malformed JSON", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await page.locator("#field-name").fill("Checked JSON endpoint");
  await page.locator("#field-path").fill("/checked-json");
  const responseBody = page.locator("#field-body");
  const responseStatus = page.locator("#field-body-json-status");

  await responseBody.fill('{"message":"ok"}');
  await page.locator("#check-field-body").click();
  await expect(responseBody).toHaveValue('{\n  "message": "ok"\n}');
  await expect(responseStatus).toHaveText("Valid JSON. The response body has been formatted.");
  await expect(responseBody).toHaveAttribute("aria-invalid", "false");

  await responseBody.fill('{"message":');
  await expect(responseStatus).toBeEmpty();
  await page.locator("#check-field-body").click();
  await expect(responseBody).toHaveValue('{"message":');
  await expect(responseStatus).toContainText("Invalid JSON:");
  await expect(responseBody).toHaveAttribute("aria-invalid", "true");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.locator("#form-error")).toHaveText("Response body contains malformed JSON.");
  await expect(responseBody).toBeFocused();
  await expect(page.getByRole("row", { name: /Checked JSON endpoint/ })).toHaveCount(0);

  await responseBody.fill('{"message":"ok"}');
  await page.locator("#field-rate-limit-enabled").check();
  await page.locator("#field-success-body").fill('{"status":');
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.locator("#form-error")).toHaveText("Success response body contains malformed JSON.");
  await expect(page.locator("#field-success-body")).toBeFocused();

  await page.locator("#field-success-body").fill('{"status":"accepted"}');
  await page.locator("#check-field-success-body").click();
  await expect(page.locator("#field-success-body")).toHaveValue('{\n  "status": "accepted"\n}');
  await expect(page.locator("#field-success-body-json-status")).toHaveText(
    "Valid JSON. The response body has been formatted."
  );
  await expectNoUnreviewedAccessibilityViolations(page, "#endpoint-dialog");
  await page.locator("#field-rate-limit-enabled").uncheck();
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.getByRole("row", { name: /Checked JSON endpoint/ })).toBeVisible();
});

test("reports server-authoritative validation errors", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await page.locator("#field-name").fill("Reserved endpoint");
  await page.locator("#field-path").fill("/health/live");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(page.getByRole("alert")).toContainText("reserved");
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("focuses the first method when endpoint method validation fails", async ({ page }) => {
  await page.getByRole("button", { name: "New endpoint" }).click();
  await page.locator("#field-name").fill("Missing method endpoint");
  await page.locator("#field-path").fill("/missing-method");
  await page.locator('#method-options input[value="GET"]').uncheck();
  await page.getByRole("button", { name: "Apply endpoint" }).click();

  await expect(page.getByRole("alert")).toHaveText("Select at least one HTTP method.");
  await expect(page.locator('#method-options input[value="GET"]')).toBeFocused();
  await expectNoUnreviewedAccessibilityViolations(page, "#endpoint-dialog");
});

test("reports denied clipboard access when copying an endpoint URL", async ({ page }) => {
  await page.evaluate(() => {
    navigator.clipboard.writeText = async () => {
      throw new DOMException("Clipboard access denied", "NotAllowedError");
    };
  });
  await page.getByRole("button", { name: "New endpoint" }).click();

  await page.locator("#copy-url").click();

  await expect(page.getByText("Clipboard access was denied. Copy the text manually.")).toBeVisible();
});

test("@smoke collapses endpoints and restores the preference", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);

  await page.getByRole("button", { name: "Collapse endpoints" }).click();
  await expect(page.locator("#endpoint-body")).toBeHidden();
  await expect(page.locator("#endpoint-controls")).toBeHidden();
  await expect(page.getByRole("button", { name: "Expand endpoints" })).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator("#endpoint-toggle svg")).toHaveCSS("transform", "matrix(0, 1, -1, 0, 0, 0)");
  await expectNoUnreviewedAccessibilityViolations(page);

  await page.reload();
  const expand = page.getByRole("button", { name: "Expand endpoints" });
  await expect(expand).toHaveAttribute("aria-expanded", "false");
  await expand.click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);
  await expect(page.locator("#endpoint-controls")).toBeVisible();
  await expect(page.locator("#endpoint-toggle")).toBeFocused();
});
