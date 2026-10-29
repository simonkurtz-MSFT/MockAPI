import { execFileSync } from "node:child_process";
import {
  expect,
  test,
  emptyDocument,
  importDocument,
  expectNoUnreviewedAccessibilityViolations,
  contrastRatio,
} from "./dashboard-fixtures.js";

test("copies an OS-aware cURL command and persists its optional fifth endpoint action", async ({ page, request }) => {
  await page.evaluate(() => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async (text) => {
          window.copiedCommand = text;
        },
      },
    });
  });
  await importDocument(request, {
    ...emptyDocument,
    endpoints: [
      {
        id: "c7635742-b795-4587-9c69-f72853b39d6c",
        name: "Copy request",
        enabled: true,
        methods: ["POST", "PUT"],
        path: "/inventory/reservations",
        response: { statusCode: 201, headers: {}, contentType: "application/json", body: "{}" },
      },
    ],
  });
  const row = page.locator(".endpoint-row");
  await expect(row.locator(".row-actions button")).toHaveCount(5);
  const duplicateIcon = await row.getByRole("button", { name: "Duplicate", exact: true }).locator("svg").innerHTML();
  expect(duplicateIcon).not.toBe(await page.locator("#copy-url svg").innerHTML());
  const copy = row.getByRole("button", { name: "Copy cURL command", exact: true });
  await copy.click();
  await expect(page.locator("#toast-region")).toContainText("cURL command copied");
  const command = await page.evaluate(() => window.copiedCommand);
  const windows = await page.evaluate(() => /win/i.test(navigator.platform));
  expect(command.startsWith(windows ? "curl.exe " : "curl ")).toBe(true);
  expect(command).toContain("--request 'POST'");
  expect(command).toContain("/inventory/reservations'");
  expect(command).toContain('--data-raw \'{"sku":"SKU-1001","quantity":2,"warehouseId":"WH-DEMO-01"}\'');
  expect(command).not.toContain("X-MockAPI-Key");
  const shell = process.platform === "win32" ? "pwsh" : "sh";
  const shellArguments = process.platform === "win32" ? ["-NoProfile", "-NonInteractive", "-Command"] : ["-c"];
  const responseBody = execFileSync(shell, [...shellArguments, command], { encoding: "utf8", timeout: 10_000 });
  expect(responseBody.trim()).toBe("{}");

  await page.route("**/__mockapi/api/security/", (route) =>
    route.fulfill({
      json: { enabled: true, configured: true, etag: '"1"' },
    })
  );
  await copy.click();
  await expect
    .poll(() => page.evaluate(() => window.copiedCommand))
    .toContain(windows ? "${env:MOCKAPI_KEY}" : "${MOCKAPI_KEY}");
  await page.evaluate(() => {
    navigator.clipboard.writeText = async () => {
      throw new Error("Denied");
    };
  });
  await copy.click();
  await expect(page.locator("#toast-region")).toContainText("Clipboard access was denied");

  await page.getByRole("button", { name: "Settings", exact: true }).click();
  const preference = page.getByRole("checkbox", { name: "Show endpoint cURL copy action" });
  await expect(preference).toBeChecked();
  await preference.uncheck();
  await expect(row.locator(".row-actions button")).toHaveCount(4);
  await page.locator("#settings-close").click();
  await page.reload();
  await expect(row.locator(".row-actions button")).toHaveCount(4);
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(preference).not.toBeChecked();
  await preference.check();
  await page.locator("#settings-close").click();
  await expect(copy).toBeVisible();
  await expectNoUnreviewedAccessibilityViolations(page, ".workspace");
});

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
    const responseStyle = await page.locator(".status-badge").evaluate((element) => {
      const computed = getComputedStyle(element);
      return {
        borderRadius: computed.borderTopLeftRadius,
        height: computed.height,
        minWidth: computed.minWidth,
      };
    });
    for (const badge of await badges.all()) {
      const style = await badge.evaluate((element) => {
        const computed = getComputedStyle(element);
        return {
          borderStyle: computed.borderTopStyle,
          borderWidth: computed.borderTopWidth,
          borderRadius: computed.borderTopLeftRadius,
          height: computed.height,
          minWidth: computed.minWidth,
          foreground: computed.color,
          background: computed.backgroundColor,
        };
      });
      expect(style.borderStyle).toBe("solid");
      expect(style.borderWidth).toBe("1px");
      expect({
        borderRadius: style.borderRadius,
        height: style.height,
        minWidth: style.minWidth,
      }).toEqual(responseStyle);
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
  const table = page.locator(".table-wrap");
  const ascendingNames = Array.from({ length: 12 }, (_, index) => `Operation ${String(index + 1).padStart(2, "0")}`);
  const ascendingPaths = Array.from({ length: 12 }, (_, index) => `/route-${String(index + 1).padStart(2, "0")}`);
  await expect(rows).toHaveCount(24);
  await expect(groups).toHaveText(["alpha", "zeta"]);
  await expect(rows.locator(".path-cell")).toHaveText([...ascendingPaths, ...ascendingPaths]);
  await expect(rows.locator(".endpoint-name-heading > strong")).toHaveText([
    ...ascendingNames.toReversed(),
    ...ascendingNames.toReversed(),
  ]);
  await expect(table.getByRole("columnheader", { name: "Path" })).toHaveAttribute("aria-sort", "ascending");
  await expect(table.getByRole("columnheader", { name: "Endpoint", exact: true })).toHaveAttribute("aria-sort", "none");
  await expect(page.locator(".table-wrap thead th").nth(1)).toHaveText("Path");
  await expect(rows.first().locator("td").nth(1)).toHaveText("/route-01");
  await expect(rows.first().locator(".path-cell")).toHaveAttribute("title", "/alpha/route-01");
  const cellStyles = await rows.first().evaluate((row) => {
    const path = getComputedStyle(row.querySelector(".path-cell"));
    const endpoint = getComputedStyle(row.querySelector(".endpoint-name-heading > strong"));
    const response = getComputedStyle(row.querySelector(".status-badge"));
    return {
      pathWeight: path.fontWeight,
      endpointWeight: endpoint.fontWeight,
      responseRadius: response.borderTopLeftRadius,
    };
  });
  expect(cellStyles).toEqual({
    pathWeight: "700",
    endpointWeight: "400",
    responseRadius: "5px",
  });
  expect((await request.get("/alpha/route-12")).status()).toBe(500);
  const stored = await (await request.get("/__mockapi/api/endpoints")).json();
  expect(stored.find((endpoint) => endpoint.id === endpoints[23].id).path).toBe("/alpha/route-12");

  await page.locator('[data-sort-key="path"]').click();
  await expect(table.getByRole("columnheader", { name: "Path" })).toHaveAttribute("aria-sort", "descending");
  await expect(rows.locator(".path-cell")).toHaveText([...ascendingPaths.toReversed(), ...ascendingPaths.toReversed()]);
  await expect(groups).toHaveText(["alpha", "zeta"]);

  await page.locator('[data-sort-key="name"]').click();
  await expect(groups).toHaveText(["alpha", "zeta"]);
  await expect(rows.locator(".endpoint-name-heading > strong")).toHaveText([...ascendingNames, ...ascendingNames]);
  await page.locator('[data-sort-key="name"]').click();
  await expect(rows.locator(".endpoint-name-heading > strong")).toHaveText([
    ...ascendingNames.toReversed(),
    ...ascendingNames.toReversed(),
  ]);
  await page.locator('[data-sort-key="response"]').click();
  await expect(groups).toHaveText(["alpha", "zeta"]);
  await expect(rows.first().locator(".status-badge")).toHaveText("500");

  await page.getByRole("button", { name: "Collapse alpha 12", exact: true }).click();
  await page.locator('[data-sort-key="path"]').click();
  await expect(rows).toHaveCount(12);
  await expect(groups).toHaveText(["alpha", "zeta"]);
  await expect(page.getByRole("button", { name: "Expand alpha 12", exact: true })).toBeVisible();
  await expect(rows.locator(".path-cell")).toHaveText(
    Array.from({ length: 12 }, (_, index) => `/route-${String(index + 1).padStart(2, "0")}`)
  );
  await page.getByRole("button", { name: "Expand alpha 12", exact: true }).click();
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

test("@smoke selects and clears one API without changing other API selections", async ({ page, request }) => {
  const endpoints = ["alpha", "zeta"].flatMap((api, groupIndex) =>
    ["GET", "POST"].map((method, index) => ({
      id: `00000000-0000-4000-8000-${String(groupIndex * 2 + index + 1).padStart(12, "0")}`,
      name: `${api} ${method}`,
      enabled: true,
      methods: [method],
      path: `/${api}/operation`,
      response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
    }))
  );
  await importDocument(request, { ...emptyDocument, endpoints });
  const alpha = page.getByRole("checkbox", { name: "Select all matching endpoints in alpha", exact: true });
  const zeta = page.getByRole("checkbox", { name: "Select all matching endpoints in zeta", exact: true });
  const selectAll = page.locator("#select-all-endpoints");
  const count = page.locator("#selection-count");
  const configuration = await request.get("/__mockapi/api/configuration");

  await expect(alpha).not.toBeChecked();
  await expect(zeta).not.toBeChecked();
  await page.getByRole("checkbox", { name: "Select zeta GET", exact: true }).check();
  await alpha.focus();
  await page.keyboard.press("Space");
  await expect(alpha).toBeChecked();
  await expect(alpha).toBeFocused();
  await expect(zeta).toBeChecked({ indeterminate: true });
  await expect(selectAll).toBeChecked({ indeterminate: true });
  await expect(count).toHaveText("3");

  await page.getByRole("checkbox", { name: "Select alpha GET", exact: true }).uncheck();
  await expect(alpha).toBeChecked({ indeterminate: true });
  await alpha.focus();
  await page.keyboard.press("Space");
  await expect(alpha).toBeChecked();
  await page.keyboard.press("Space");
  await expect(alpha).not.toBeChecked();
  await expect(count).toHaveText("1");
  await expect(page.getByRole("checkbox", { name: "Select zeta GET", exact: true })).toBeChecked();

  const alphaToggle = page.getByRole("button", { name: "Collapse alpha 2", exact: true });
  await expect(alphaToggle).toHaveText("alpha 2");
  await alphaToggle.click();
  await alpha.check();
  await expect(page.locator(".endpoint-row")).toHaveCount(2);
  await expect(page.getByRole("button", { name: "Expand alpha 2", exact: true })).toBeVisible();
  await expect(count).toHaveText("3");
  await selectAll.check();
  await expect(alpha).toBeChecked();
  await expect(zeta).toBeChecked();
  await expect(count).toHaveText("4");
  await selectAll.uncheck();
  await expect(alpha).not.toBeChecked();
  await expect(zeta).not.toBeChecked();
  await expect(count).toHaveText("0");

  await zeta.check();
  await expect(count).toHaveText("2");
  const afterSelection = await request.get("/__mockapi/api/configuration");
  expect(await afterSelection.json()).toEqual(await configuration.json());
  await expectNoUnreviewedAccessibilityViolations(page, ".workspace");
  await page.locator("#bulk-disable").click();
  await expect(page.getByText("Disabled 2 endpoints", { exact: true })).toBeVisible();
  const stored = await (await request.get("/__mockapi/api/endpoints")).json();
  expect(stored.filter((endpoint) => endpoint.enabled).map((endpoint) => endpoint.id)).toEqual(
    endpoints.filter((endpoint) => endpoint.path.startsWith("/alpha/")).map((endpoint) => endpoint.id)
  );
});

test("per-API selection respects filters and preserves hidden selections", async ({ page, request }) => {
  const endpoints = ["alpha", "zeta"].flatMap((api, groupIndex) =>
    ["GET", "POST"].map((method, index) => ({
      id: `00000000-0000-4000-8000-${String(groupIndex * 2 + index + 1).padStart(12, "0")}`,
      name: `${api} ${method}`,
      enabled: true,
      methods: [method],
      path: `/${api}/operation`,
      response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
    }))
  );
  await importDocument(request, { ...emptyDocument, endpoints });
  const alpha = page.getByRole("checkbox", { name: "Select all matching endpoints in alpha", exact: true });
  const zeta = page.getByRole("checkbox", { name: "Select all matching endpoints in zeta", exact: true });
  const count = page.locator("#selection-count");
  await page.getByRole("checkbox", { name: "Select alpha POST", exact: true }).check();
  await page.getByRole("checkbox", { name: "Select zeta POST", exact: true }).check();
  await page.locator("#filter-method").selectOption("GET");
  await expect(page.locator('[data-endpoint-group="alpha"]')).toHaveAccessibleName("Collapse alpha 1");
  await expect(alpha).not.toBeChecked();
  await expect(zeta).not.toBeChecked();
  await alpha.check();
  await expect(count).toHaveText("3");
  await expect(page.getByRole("checkbox", { name: "Select alpha GET", exact: true })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Select zeta GET", exact: true })).not.toBeChecked();
  await alpha.uncheck();
  await expect(count).toHaveText("2");
  await page.locator("#filter-method").selectOption("");
  await expect(page.locator('[data-endpoint-group="alpha"]')).toHaveAccessibleName("Collapse alpha 2");
  await expect(alpha).toBeChecked({ indeterminate: true });
  await expect(zeta).toBeChecked({ indeterminate: true });
  await expect(page.getByRole("checkbox", { name: "Select alpha POST", exact: true })).toBeChecked();
  await expect(page.getByRole("checkbox", { name: "Select zeta POST", exact: true })).toBeChecked();

  await page.locator("#filter-text").fill("/alpha");
  await alpha.check();
  await expect(count).toHaveText("3");
  await page.locator("#filter-text").fill("no matches");
  await expect(page.locator(".endpoint-group-selection")).toHaveCount(0);
  await expect(page.locator("#select-all-endpoints")).toBeDisabled();
  await expect(count).toHaveText("3");
  await page.locator("#filter-text").fill("");
  await expect(alpha).toBeChecked();
  await expect(zeta).toBeChecked({ indeterminate: true });
  await expect(count).toHaveText("3");
});

test("@smoke collapses and expands all API groups without changing endpoint configuration or selection", async ({
  page,
  request,
}) => {
  const control = page.locator("#endpoint-toggle-groups");
  await expect(control).toHaveAccessibleName("Collapse all groups");
  await expect(control).toBeDisabled();
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const rows = page.locator("#endpoint-rows .endpoint-row");
  const groups = page.locator(".endpoint-group-toggle");
  await expect(rows).toHaveCount(41);
  await expect(control).toBeEnabled();
  await rows.first().locator(".selection-checkbox").check();
  const configuration = await request.get("/__mockapi/api/configuration");
  const pathSort = page.locator('[data-sort-key="path"]');
  const pathHeader = page.locator("th").filter({ has: pathSort });
  await pathSort.click();
  await expect(pathHeader).toHaveAttribute("aria-sort", "descending");
  await expect(page.locator('.endpoint-group-toggle[aria-expanded="true"]')).toHaveCount(3);

  await control.focus();
  await page.keyboard.press("Enter");
  await expect(control).toHaveAccessibleName("Expand all groups");
  await expect(control).toHaveAttribute("title", "Expand all groups");
  await expect(control).toHaveAttribute("aria-expanded", "false");
  await expect(control.locator(".endpoint-group-chevron")).toHaveCSS("transform", "matrix(1, 0, 0, 1, 0, 0)");
  await expect(pathHeader).toHaveAttribute("aria-sort", "descending");
  await expect(control).toBeFocused();
  await expect(rows).toHaveCount(0);
  await expect(groups).toHaveCount(3);
  await expect(page.locator('.endpoint-group-toggle[aria-expanded="false"]')).toHaveCount(3);
  await expect(page.locator("#endpoint-count")).toHaveText("41");
  await expect(page.locator("#selection-count")).toHaveText("1");
  await expectNoUnreviewedAccessibilityViolations(page, ".workspace");

  await page.keyboard.press("Space");
  await expect(rows).toHaveCount(41);
  await expect(control).toHaveAccessibleName("Collapse all groups");
  await expect(control).toHaveAttribute("title", "Collapse all groups");
  await expect(control).toHaveAttribute("aria-expanded", "true");
  await expect(control.locator(".endpoint-group-chevron")).toHaveCSS("transform", "matrix(0, 1, -1, 0, 0, 0)");
  await expect(control).toBeFocused();
  await pathSort.click();
  await expect(pathHeader).toHaveAttribute("aria-sort", "ascending");
  await expect(page.locator('.endpoint-group-toggle[aria-expanded="true"]')).toHaveCount(3);
  await expect(rows.locator(".selection-checkbox:checked")).toHaveCount(1);
  await page.getByRole("button", { name: "Collapse inventory 11", exact: true }).click();
  await expect(control).toHaveAccessibleName("Collapse all groups");
  await expect(rows).toHaveCount(30);
  await control.click();
  await expect(rows).toHaveCount(0);
  await expect(control).toHaveAccessibleName("Expand all groups");
  await page.getByRole("button", { name: "Expand aoai 16", exact: true }).click();
  await expect(rows).toHaveCount(16);
  await expect(control).toHaveAccessibleName("Collapse all groups");
  await control.click();
  await expect(rows).toHaveCount(0);
  await page.getByRole("button", { name: "Collapse endpoints", exact: true }).click();
  await expect(control).toBeHidden();
  await page.getByRole("button", { name: "Expand endpoints", exact: true }).click();
  await expect(control).toHaveAccessibleName("Expand all groups");
  await control.click();
  await expect(rows).toHaveCount(41);
  await expect(rows.locator(".selection-checkbox:checked")).toHaveCount(1);

  const current = await request.get("/__mockapi/api/configuration");
  expect(current.headers().etag).toBe(configuration.headers().etag);
  await expectNoUnreviewedAccessibilityViolations(page, ".workspace");
});

test("remembers API group state across refreshes only in the tab session and honors the Settings opt-out", async ({
  page,
  context,
}) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const rows = page.locator(".endpoint-row");
  await expect(rows).toHaveCount(41);
  await page.getByRole("button", { name: "Collapse inventory 11", exact: true }).click();
  await page.reload();
  await expect(page.getByRole("button", { name: "Expand inventory 11", exact: true })).toBeVisible();
  await expect(rows).toHaveCount(30);
  expect(await page.evaluate(() => JSON.parse(sessionStorage.getItem("mockapi.endpointGroups")))).toEqual([
    "inventory",
  ]);
  const preferences = await page.evaluate(() => JSON.parse(localStorage.getItem("mockapi.preferences")));
  expect(preferences).not.toHaveProperty("collapsedEndpointGroups");
  expect(await page.evaluate(() => localStorage.getItem("mockapi.endpointGroups"))).toBeNull();

  const otherTab = await context.newPage();
  await otherTab.goto("/");
  await expect(otherTab.locator(".endpoint-row")).toHaveCount(41);
  await otherTab.close();

  await page.locator("#filter-text").fill("/shipping/");
  await page.locator("#endpoint-toggle-groups").click();
  await page.reload();
  await expect(rows).toHaveCount(16);
  await expect(page.getByRole("button", { name: "Expand shipping 14", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  const remember = page.getByRole("checkbox", { name: "Remember expanded/collapsed API groups for this session" });
  await expect(remember).toBeChecked();
  await remember.uncheck();
  expect(await page.evaluate(() => sessionStorage.getItem("mockapi.endpointGroups"))).toBeNull();
  await page.locator("#settings-close").click();
  await expect(rows).toHaveCount(16);
  await page.reload();
  await expect(rows).toHaveCount(41);
  await page.getByRole("button", { name: "Collapse inventory 11", exact: true }).click();
  await page.reload();
  await expect(rows).toHaveCount(41);
  await page.getByRole("button", { name: "Collapse inventory 11", exact: true }).click();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(remember).not.toBeChecked();
  await remember.check();
  await page.locator("#settings-close").click();
  await page.reload();
  await expect(rows).toHaveCount(30);
  await page.locator("#endpoint-toggle-groups").click();
  await page.reload();
  await expect(page.locator("#endpoint-toggle-groups")).toHaveAccessibleName("Expand all groups");
  await expect(rows).toHaveCount(0);
  await page.locator("#endpoint-toggle-groups").click();
  await page.reload();
  await expect(rows).toHaveCount(41);
});

test("reports unavailable session storage while keeping API group controls usable", async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, "sessionStorage", {
      get() {
        throw new Error("Session storage unavailable");
      },
    });
  });
  await page.reload();
  await expect(page.locator("#toast-region")).toContainText("Unable to restore endpoint groups");
  await expect(page.locator("#connection-status")).toHaveText("Live");
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator(".endpoint-row")).toHaveCount(41);
  await page.getByRole("button", { name: "Collapse inventory 11", exact: true }).click();
  await expect(page.locator("#toast-region")).toContainText("Unable to save endpoint groups");
  await expect(page.locator(".endpoint-row")).toHaveCount(30);
  await expect(page.getByRole("button", { name: "Expand inventory 11", exact: true })).toBeVisible();
});

test("bulk API group collapse applies to filtered groups and preserves hidden group state", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const control = page.locator("#endpoint-toggle-groups");
  const filter = page.locator("#filter-text");
  const rows = page.locator("#endpoint-rows .endpoint-row");
  await expect(rows).toHaveCount(41);
  await filter.fill("/inventory/");
  await expect(rows).toHaveCount(11);
  await control.click();
  await expect(rows).toHaveCount(0);
  await expect(control).toHaveAccessibleName("Expand all groups");
  await filter.fill("");
  await expect(rows).toHaveCount(30);
  await expect(control).toHaveAccessibleName("Collapse all groups");
  await expect(page.getByRole("button", { name: "Expand inventory 11", exact: true })).toBeVisible();
  await filter.fill("no matching API group");
  await expect(page.locator(".endpoint-group-toggle")).toHaveCount(0);
  await expect(control).toBeDisabled();
  await filter.fill("/shipping/");
  await expect(rows).toHaveCount(14);
  await expect(control).toBeEnabled();
  await control.click();
  await expect(rows).toHaveCount(0);
  await filter.fill("");
  await expect(rows).toHaveCount(16);
  await expect(page.getByRole("button", { name: "Expand inventory 11", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Expand shipping 14", exact: true })).toBeVisible();
  await control.click();
  await expect(rows).toHaveCount(0);
  await control.click();
  await expect(rows).toHaveCount(41);
});

test("@smoke loads examples idempotently and filters by status", async ({ page, request }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(41);
  await expect(page.locator(".endpoint-group-row")).toHaveCount(3);
  await expect(page.getByRole("button", { name: "Collapse aoai 16", exact: true })).toContainText("16");
  await expect(page.getByRole("navigation", { name: "Endpoint pages" })).toHaveCount(0);
  const responseHeader = page.getByRole("columnheader", { name: "Response" });
  await responseHeader.getByRole("button").click();
  await responseHeader.getByRole("button").click();
  await expect(responseHeader).toHaveAttribute("aria-sort", "descending");
  await expect(page.locator("#endpoint-rows .endpoint-row").first().locator(".status-badge")).toHaveText("DROP");
  await expect(page.locator("#endpoint-rows .endpoint-row").nth(16).locator(".status-badge")).toHaveText("503");
  await expect(page.locator("#endpoint-rows .endpoint-row").nth(27).locator(".status-badge")).toHaveText("DROP");
  await page.getByRole("columnheader", { name: "Endpoint" }).getByRole("button").click();
  const exampleGroup = page.getByRole("button", { name: "Collapse inventory" });
  await expect(exampleGroup).toContainText("11");
  await page.locator("#endpoint-rows .endpoint-row").first().locator(".selection-checkbox").check();
  await expect(page.locator("#selection-count")).toHaveText("1");
  await exampleGroup.click();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(30);
  await expect(page.locator("#selection-count")).toHaveText("1");
  await page.locator("#select-all-endpoints").check();
  await expect(page.locator("#selection-count")).toHaveText("41");
  await page.getByRole("button", { name: "Expand inventory" }).click();
  await expect(page.locator("#endpoint-rows .selection-checkbox:checked")).toHaveCount(41);
  await page.locator("#select-all-endpoints").uncheck();
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.getByText("No example changes were needed")).toBeVisible();
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(41);
  const completion = await request.post("/aoai/200/chat/completions", {
    data: { messages: [{ role: "user", content: "How many Trail flasks are available?" }] },
  });
  expect(completion.status()).toBe(200);
  expect((await completion.json()).choices[0].message.content).toContain("Trailhead Supply");

  await page.locator("#filter-status").selectOption("3");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(0);

  await page.locator("#filter-status").selectOption("5");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(7);
  await expect(page.getByRole("row", { name: /Warehouse outage fault demo/ }).locator(".status-badge")).toHaveText(
    "503"
  );
  await expect(page.getByRole("row", { name: /Carrier timeout fault demo/ }).locator(".status-badge")).toHaveText(
    "504"
  );
  const serverError = await request.get("/shipping/demo-faults/carrier-timeout");
  expect(serverError.status()).toBe(504);
  expect(await serverError.json()).toEqual({
    type: "about:blank",
    title: "Gateway Timeout",
    status: 504,
    detail: "The sample carrier did not respond before the fictional gateway deadline.",
  });

  await page.locator("#filter-status").selectOption("4");
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(15);
  await expect(
    page.getByRole("row", { name: /Reservation validation fault demo/ }).locator(".status-badge")
  ).toHaveText("422");
  await expect(page.getByRole("row", { name: /Insufficient stock fault demo/ }).locator(".status-badge")).toHaveText(
    "409"
  );
  const rateLimitedRow = page.locator("#endpoint-rows").getByRole("row", { name: /Tracking rate-limit fault demo/ });
  await expect(rateLimitedRow.locator(".endpoint-name-heading > strong")).toHaveText("Tracking rate-limit fault demo");
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
  await page.getByLabel("Delay in seconds").fill("2");
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
  await expect(page.getByLabel("Delay in seconds")).toHaveValue("2");
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
  await expect(page.locator(".endpoint-row")).toHaveCount(41);
  for (const width of [1200, 1920]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(page.locator("html")).toHaveAttribute("data-dashboard-layout", width === 1200 ? "stacked" : "columns");
    const headers = page.locator(".table-wrap thead th");
    await expect(headers).toHaveCount(8);
    const original = await headers.evaluateAll((cells) =>
      cells.map((cell) => ({ x: cell.getBoundingClientRect().x, width: cell.getBoundingClientRect().width }))
    );
    expect(original[1].width).toBeLessThan(310);
    expect(original[2].width).toBeGreaterThan(original[4].width * 2);
    const groupCell = await page
      .locator(".endpoint-group-row > th")
      .first()
      .evaluate((cell) => {
        const computed = getComputedStyle(cell);
        return {
          paddingInlineStart: computed.paddingInlineStart,
          x: cell.getBoundingClientRect().x,
        };
      });
    const groupCaret = await page.locator(".endpoint-group-chevron").first().boundingBox();
    const groupSelection = await page.locator(".endpoint-group-selection-control").first().boundingBox();
    expect(groupCell.paddingInlineStart).toBe("0px");
    expect(groupSelection.x).toBe(groupCell.x);
    expect(groupSelection.width).toBe(34);
    expect(groupCaret.x).toBeGreaterThanOrEqual(groupSelection.x + groupSelection.width);
    expect(groupCaret.x - (groupSelection.x + groupSelection.width)).toBeLessThanOrEqual(14);
    for (const [column, maximumWidth] of [
      [4, 80],
      [5, 80],
      [6, 72],
      [7, 196],
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
    await page.getByRole("button", { name: "Collapse inventory 11", exact: true }).click();
    await expect(page.locator(".endpoint-row")).toHaveCount(30);
    const collapsed = await headers.evaluateAll((cells) =>
      cells.map((cell) => ({ x: cell.getBoundingClientRect().x, width: cell.getBoundingClientRect().width }))
    );
    expect(collapsed).toEqual(original);
    await page.getByRole("button", { name: "Expand inventory 11", exact: true }).click();
    await expect(page.locator(".endpoint-row")).toHaveCount(41);
    const expanded = await headers.evaluateAll((cells) =>
      cells.map((cell) => ({ x: cell.getBoundingClientRect().x, width: cell.getBoundingClientRect().width }))
    );
    // Clicking a sort button may scroll the table horizontally; compare column widths.
    expect(expanded.map((cell) => cell.width)).toEqual(original.map((cell) => cell.width));
    await page.locator("#endpoint-toggle-groups").click();
    await expect(page.locator(".endpoint-row")).toHaveCount(0);
    expect(await headers.evaluateAll((cells) => cells.map((cell) => cell.getBoundingClientRect().width))).toEqual(
      original.map((cell) => cell.width)
    );
    await page.locator("#endpoint-toggle-groups").click();
    await expect(page.locator(".endpoint-row")).toHaveCount(41);
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

test("selects multiple or all endpoints beyond former count limits for bulk actions", async ({ page, request }) => {
  const endpoints = Array.from({ length: 13 }, (_, index) => ({
    id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
    name: `Custom bulk endpoint ${index + 1}`,
    enabled: true,
    methods: ["GET"],
    path: `/custom-bulk/${index + 1}`,
    response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
  }));
  await importDocument(request, { ...emptyDocument, endpoints });
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(13);
  await page.getByRole("button", { name: "Load examples" }).first().click();
  const rows = page.locator("#endpoint-rows .endpoint-row");
  await expect(rows).toHaveCount(54);

  await rows.nth(0).locator(".selection-checkbox").check();
  await rows.nth(1).locator(".selection-checkbox").check();
  await expect(page.locator("#selection-count")).toHaveText("2");
  await expectNoUnreviewedAccessibilityViolations(page);
  await page.getByRole("button", { name: "Disable" }).click();
  await expect(page.getByText("Disabled 2 endpoints", { exact: true })).toBeVisible();
  await expect(rows.nth(0).getByRole("checkbox", { name: /^Enable |^Disable / })).not.toBeChecked();
  await expect(rows.nth(1).getByRole("checkbox", { name: /^Enable |^Disable / })).not.toBeChecked();

  await page.locator("#select-all-endpoints").check();
  await expect(page.locator("#selection-count")).toHaveText("54");
  await page.getByRole("button", { name: "Enable", exact: true }).click();
  await expect(page.getByText("Enabled 54 endpoints", { exact: true })).toBeVisible();
  await expect(rows.getByRole("checkbox", { name: /^Disable / })).toHaveCount(54);

  await page.locator("#select-all-endpoints").check();
  await page.locator("#bulk-delete").click();
  await expect(page.getByRole("dialog", { name: "Delete 54 selected endpoints" })).toBeVisible();
  await page
    .getByRole("dialog", { name: "Delete 54 selected endpoints" })
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
  endpoints.push({
    id: "d90d0627-597c-45e1-a77d-22a3bc459aa0",
    name: "Transport failure",
    enabled: true,
    methods: ["GET"],
    path: "/transport-failure",
    response: { behavior: "abortConnection", headers: {}, contentType: null, body: "" },
  });
  await importDocument(request, { ...emptyDocument, endpoints });

  for (const theme of ["light", "dark"]) {
    await page.goto(`/?scoutTheme=${theme}`);
    for (const endpoint of endpoints) {
      const statusCode = endpoint.response.statusCode;
      const badge = page.getByRole("row", { name: new RegExp(endpoint.name) }).locator(".status-badge");
      if (statusCode) await expect(badge).toHaveClass(new RegExp(`status-${Math.floor(statusCode / 100)}xx`));
      await expect(badge).toHaveText(statusCode ? String(statusCode) : "DROP");
      const colors = await badge.evaluate((element, statusCode) => {
        const forcedColors = matchMedia("(forced-colors: active)").matches;
        const colorProbe = document.createElement("span");
        const statusToken = statusCode ? `--status-${Math.floor(statusCode / 100)}xx` : "--status-other";
        colorProbe.style.backgroundColor = forcedColors ? "Highlight" : `var(${statusToken})`;
        document.body.append(colorProbe);
        const colors = {
          forcedColors,
          expectedBackground: getComputedStyle(colorProbe).backgroundColor,
          background: getComputedStyle(element).backgroundColor,
          foreground: getComputedStyle(element).color,
        };
        colorProbe.remove();
        return colors;
      }, statusCode);
      if (!colors.forcedColors) {
        expect(colors.background).toBe(colors.expectedBackground);
      }
      expect(contrastRatio(colors.foreground, colors.background)).toBeGreaterThanOrEqual(4.5);
      await badge.hover();
      const hoveredColors = await badge.evaluate((element) => ({
        background: getComputedStyle(element).backgroundColor,
        foreground: getComputedStyle(element).color,
      }));
      expect(hoveredColors.background).toBe(colors.background);
      expect(contrastRatio(hoveredColors.foreground, hoveredColors.background)).toBeGreaterThanOrEqual(4.5);
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
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(41);

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
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(41);
  await expect(page.locator("#endpoint-controls")).toBeVisible();
  await expect(page.locator("#endpoint-toggle")).toBeFocused();
});
