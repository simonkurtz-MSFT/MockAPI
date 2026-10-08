import {
  expect,
  test,
  emptyDocument,
  importDocument,
  expectNoUnreviewedAccessibilityViolations,
  contrastRatio,
} from "./dashboard-fixtures.js";

test("@smoke loads examples idempotently and filters by status", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(9);
  await expect(page.locator("#endpoint-page-status")).toHaveText("1–9 of 9");
  await expect(page.locator("#endpoint-page-previous")).toBeDisabled();
  await expect(page.locator("#endpoint-page-next")).toBeDisabled();
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
  await page.locator("#field-name").fill("Edited browser endpoint");
  await page.locator("#field-description").fill("Updated through the dashboard");
  await page.getByRole("button", { name: "Apply endpoint" }).click();
  const editedRow = page.getByRole("row", { name: /Edited browser endpoint/ });
  await expect(editedRow).toBeVisible();
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
