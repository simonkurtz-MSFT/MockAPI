import { mkdir, readFile, rmdir, unlink, writeFile } from "node:fs/promises";
import path from "node:path";
import {
  expect,
  test,
  emptyDocument,
  importDocument,
  expectNoUnreviewedAccessibilityViolations,
} from "./dashboard-fixtures.js";

test("imports, automatically saves, and exports configuration", async ({ page, request }) => {
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
  await expect(page.getByRole("button", { name: "Retry save" })).toBeDisabled();
  const status = await request.get("/__mockapi/api/configuration");
  expect((await status.json()).hasUnsavedChanges).toBe(false);

  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("link", { name: "Export" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe("mockapi.json");

  await page.getByLabel("Export format").selectOption("postman");
  const postmanDownloadPromise = page.waitForEvent("download");
  await page.getByRole("link", { name: "Export" }).click();
  const postmanDownload = await postmanDownloadPromise;
  expect(postmanDownload.suggestedFilename()).toBe("mockapi.postman_collection.json");

  await page.getByLabel("Export format").selectOption("openapi");
  const openApiDownloadPromise = page.waitForEvent("download");
  await page.getByRole("link", { name: "Export" }).click();
  const openApiDownload = await openApiDownloadPromise;
  expect(openApiDownload.suggestedFilename()).toBe("mockapi.openapi.json");
  await page
    .getByRole("row", { name: /Imported endpoint/ })
    .getByRole("button", { name: "Edit", exact: true })
    .click();
  await expect(page.locator("#endpoint-dialog")).toBeVisible();
  await expect(page.locator("#field-name")).toHaveValue("Imported endpoint");
});

test("an automatic save failure keeps the endpoint active and offers persistence-only retry", async ({
  page,
  request,
}) => {
  const configurationPath = path.resolve("artifacts", "playwright", "mockapi.json");
  const previousDocument = await readFile(configurationPath);
  let blocked = false;
  let saved = false;
  try {
    await unlink(configurationPath);
    await mkdir(configurationPath);
    blocked = true;

    await page.getByRole("button", { name: "New endpoint" }).click();
    await page.locator("#field-name").fill("Active but unsaved");
    await page.locator("#field-path").fill("/autosave-failure-browser");
    await page.locator("#field-content-type").fill("text/plain");
    await page.locator("#field-body").fill("active response");
    await page.getByRole("button", { name: "Apply endpoint" }).click();

    await expect(page.locator("#endpoint-dialog")).toBeHidden();
    await expect(page.getByRole("row", { name: /Active but unsaved/ })).toBeVisible();
    await expect(page.locator(".toast.error")).toContainText("change is active");
    const retry = page.getByRole("button", { name: "Retry save" });
    await expect(retry).toBeEnabled();
    await expect(retry).toHaveAttribute("title", /unsaved changes/);
    const active = await request.get("/autosave-failure-browser");
    expect(await active.text()).toBe("active response");
    const before = await (await request.get("/__mockapi/api/configuration")).json();
    expect(before.hasUnsavedChanges).toBe(true);

    await rmdir(configurationPath);
    blocked = false;
    await retry.click();
    await expect(retry).toBeDisabled();
    await expect(page.locator(".toast").filter({ hasText: "Configuration saved" })).toBeVisible();
    const after = await (await request.get("/__mockapi/api/configuration")).json();
    expect(after.revision).toBe(before.revision);
    expect(after.hasUnsavedChanges).toBe(false);
    const document = JSON.parse(await readFile(configurationPath, "utf8"));
    expect(document.endpoints).toHaveLength(1);
    expect(document.endpoints[0].path).toBe("/autosave-failure-browser");
    saved = true;
  } finally {
    if (blocked) await rmdir(configurationPath);
    if (!saved) await writeFile(configurationPath, previousDocument);
  }
});

test("@smoke opens the current mock specification in a browser tab through the live OpenAPI link", async ({
  page,
  request,
}) => {
  const endpoint = {
    id: "91360a7d-60ef-4570-9da5-fd9f86fbb067",
    name: "Live OpenAPI endpoint",
    enabled: true,
    methods: ["GET"],
    path: "/live-openapi",
    response: {
      statusCode: 200,
      headers: {},
      contentType: "text/plain",
      body: "original",
    },
  };
  const disabled = {
    ...endpoint,
    id: "27ff7446-5164-419c-a4ef-e320dd363feb",
    path: "/disabled-openapi",
    enabled: false,
  };
  await importDocument(request, { ...emptyDocument, endpoints: [endpoint, disabled] });

  const link = page
    .locator(".workspace-toolbar")
    .getByRole("link", { name: "Live OpenAPI JSON (opens in a new tab)", exact: true });
  await expect(link).toBeVisible();
  const specificationPath = "/__mockapi/api/configuration/export/openapi?download=false";
  await expect(link).toHaveAttribute("href", specificationPath);
  await expect(link).toHaveAttribute("target", "_blank");
  await expect(link).toHaveAttribute("rel", "noopener noreferrer");
  const description =
    "OpenAPI 3.1.0 JSON. Updated automatically from enabled mock endpoints on every request, without saving, rebuilding, or restarting. Opens in a new tab.";
  await expect(link).toHaveAttribute("title", description);
  await expect(link).toHaveAccessibleDescription(description);
  await expect(page.locator(".openapi-reference")).toHaveCount(0);
  const logo = link.locator("img");
  await expect(logo).toHaveAttribute(
    "src",
    /\/__mockapi\/openapi-logo\.svg\?v=[A-F0-9]{64}#svgView\(viewBox\(44,123,115,115\)\)/
  );
  await expect(logo).toHaveAttribute("alt", "");
  for (const theme of ["light", "dark"]) {
    await page.evaluate((value) => document.documentElement.setAttribute("data-theme", value), theme);
    await expect.poll(() => logo.evaluate((image) => image.complete && image.naturalWidth > 0)).toBe(true);
    const headingBounds = await page.getByRole("heading", { name: /^Endpoints/ }).boundingBox();
    const linkBounds = await link.boundingBox();
    const logoBounds = await logo.boundingBox();
    expect(linkBounds.x - (headingBounds.x + headingBounds.width)).toBeGreaterThanOrEqual(18);
    expect(linkBounds.width).toBe(30);
    expect(linkBounds.height).toBe(30);
    await expect(link).toHaveCSS("margin-left", "8px");
    expect(logoBounds.width).toBe(22);
    expect(logoBounds.height).toBe(22);
    await link.hover();
    await expectNoUnreviewedAccessibilityViolations(page, ".endpoint-heading");
  }
  await page.getByLabel("Export format").selectOption("postman");

  const downloads = [];
  page.on("download", (download) => downloads.push(download));

  async function openSpecification() {
    await link.focus();
    await expect(link).toBeFocused();
    const popupPromise = page.waitForEvent("popup");
    await link.press("Enter");
    const popup = await popupPromise;
    try {
      await expect(popup).toHaveURL(new URL(specificationPath, page.url()).href);
      await expect(popup.locator("pre")).toBeVisible();
      expect(await popup.evaluate(() => window.opener)).toBeNull();
      return JSON.parse(await popup.locator("pre").innerText());
    } finally {
      await popup.close();
    }
  }

  const initial = await openSpecification();
  expect(initial.openapi).toBe("3.1.0");
  expect(Object.keys(initial.paths)).toEqual(["/live-openapi"]);
  expect(initial.paths["/live-openapi"].get.responses["200"].content["text/plain"].example).toBe("original");

  await importDocument(request, {
    ...emptyDocument,
    endpoints: [{ ...endpoint, response: { ...endpoint.response, body: "updated without saving" } }],
  });
  const updated = await openSpecification();
  expect(updated.paths["/live-openapi"].get.responses["200"].content["text/plain"].example).toBe(
    "updated without saving"
  );

  await importDocument(request, { ...emptyDocument, endpoints: [{ ...endpoint, enabled: false }] });
  expect((await openSpecification()).paths).toEqual({});
  expect(downloads).toHaveLength(0);
  await expectNoUnreviewedAccessibilityViolations(page, ".endpoint-heading");
});

test.describe("revision-bound management commands", () => {
  const endpoint = {
    id: "17b8933c-aa77-48e3-b950-2764a479ec95",
    name: "Reviewed endpoint",
    enabled: true,
    methods: ["GET"],
    path: "/reviewed-endpoint",
    response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "reviewed" },
  };

  async function arrangeEndpoint(page, request) {
    await importDocument(request, { ...emptyDocument, endpoints: [endpoint] });
    const row = page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`);
    await expect(row).toContainText(endpoint.name);
    const configuration = await (await request.get("/__mockapi/api/configuration")).json();
    return { row, etag: configuration.etag };
  }

  async function editExternally(page, request) {
    const changed = { ...endpoint, name: "Changed externally", response: { ...endpoint.response, body: "newer" } };
    await importDocument(request, { ...emptyDocument, endpoints: [changed] });
    await expect(page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`)).toContainText(changed.name);
    return changed;
  }

  test("rejects a stale editor draft after a live update and permits an explicitly reopened edit", async ({
    page,
    request,
  }) => {
    const { row, etag } = await arrangeEndpoint(page, request);
    await row.getByRole("button", { name: "Edit", exact: true }).click();
    await page.locator("#field-name").fill("Local draft");
    const changed = await editExternally(page, request);
    const writes = [];
    page.on("request", (request) => {
      if (request.method() === "PUT" && request.url().endsWith(`/endpoints/${endpoint.id}`)) writes.push(request);
    });

    const rejected = page.waitForResponse(
      (response) => response.request().method() === "PUT" && response.url().endsWith(`/endpoints/${endpoint.id}`)
    );
    await page.getByRole("button", { name: "Apply endpoint" }).click();
    expect((await rejected).status()).toBe(412);
    await expect(page.locator("#form-error")).toBeVisible();
    await expect(page.getByRole("button", { name: "Apply endpoint" })).toBeEnabled();
    expect(writes).toHaveLength(1);
    expect(writes[0].headers()["if-match"]).toBe(etag);
    expect(await (await request.get(`/__mockapi/api/endpoints/${endpoint.id}`)).json()).toMatchObject(changed);
    await expect(page.locator("#field-name")).toHaveValue("Local draft");

    await page.locator("#endpoint-dialog").press("Escape");
    await row.getByRole("button", { name: "Edit", exact: true }).click();
    await expect(page.locator("#field-name")).toHaveValue(changed.name);
    await page.locator("#field-name").fill("Reviewed again");
    await page.getByRole("button", { name: "Apply endpoint" }).click();
    await expect(row).toContainText("Reviewed again");
  });

  for (const operation of ["delete", "bulk delete", "import"]) {
    test(`rejects a stale ${operation} confirmation after a live update`, async ({ page, request }) => {
      const { row, etag } = await arrangeEndpoint(page, request);
      let path;
      if (operation === "delete") {
        await row.getByRole("button", { name: "Delete", exact: true }).click();
        path = `/endpoints/${endpoint.id}`;
      } else if (operation === "bulk delete") {
        await row.getByRole("checkbox", { name: `Select ${endpoint.name}`, exact: true }).check();
        await page.locator("#bulk-delete").click();
        path = "/endpoints/bulk";
      } else {
        await page.locator("#import-file").setInputFiles({
          name: "empty.json",
          mimeType: "application/json",
          buffer: Buffer.from(JSON.stringify(emptyDocument)),
        });
        path = "/configuration/import";
      }
      await expect(page.locator("#confirm-dialog")).toBeVisible();
      await expect(page.locator("#confirm-accept")).toBeEnabled();
      const changed = await editExternally(page, request);
      const rejected = page.waitForResponse(
        (response) => response.request().method() !== "GET" && response.url().endsWith(path)
      );

      await page.locator("#confirm-accept").click();
      const response = await rejected;
      expect(response.status()).toBe(412);
      expect(response.request().headers()["if-match"]).toBe(etag);
      await expect(page.locator("#toast-region .error")).toBeVisible();
      expect(await (await request.get("/__mockapi/api/endpoints")).json()).toMatchObject([changed]);
    });
  }

  test("rejects a stale force-merge preview rather than applying unreviewed conflicts", async ({ page, request }) => {
    const example = await (await request.get("/__mockapi/api/configuration/example")).json();
    const hello = example.endpoints.find((candidate) => candidate.path === "/ex/hello");
    const locallyChanged = { ...hello, response: { ...hello.response, body: "local change" } };
    await importDocument(request, { ...emptyDocument, endpoints: [locallyChanged] });
    await expect(page.locator(`.endpoint-row[data-endpoint-id="${hello.id}"]`)).toBeVisible();
    const { etag } = await (await request.get("/__mockapi/api/configuration")).json();
    await page.locator("#load-example-button").click();
    await expect(page.getByRole("heading", { name: "Built-in configuration conflicts" })).toBeVisible();
    await expect(page.locator("#confirm-accept")).toBeEnabled();
    const newer = { ...locallyChanged, name: "Newer conflict" };
    await importDocument(request, { ...emptyDocument, endpoints: [newer] });
    await expect(page.locator(`.endpoint-row[data-endpoint-id="${hello.id}"]`)).toContainText(newer.name);

    const rejected = page.waitForResponse((response) =>
      response.url().endsWith("/configuration/example/merge?force=true")
    );
    await page.getByRole("button", { name: "Force update" }).click();
    const response = await rejected;
    expect(response.status()).toBe(412);
    expect(response.request().headers()["if-match"]).toBe(etag);
    await expect(page.locator("#toast-region .error")).toBeVisible();
    expect(await (await request.get("/__mockapi/api/endpoints")).json()).toMatchObject([newer]);
  });

  test("sends one write for repeated form submissions and restores controls after failure", async ({ page }) => {
    let release;
    const pending = new Promise((resolve) => {
      release = resolve;
    });
    let writes = 0;
    await page.route("**/__mockapi/api/endpoints", async (route) => {
      if (route.request().method() !== "POST") return route.continue();
      writes += 1;
      await pending;
      await route.fulfill({
        status: 503,
        contentType: "application/problem+json",
        body: JSON.stringify({ detail: "Write unavailable" }),
      });
    });
    await page.getByRole("button", { name: "New endpoint" }).click();
    await page.locator("#field-name").fill("Single submission");
    await page.locator("#field-path").fill("/single-submission");
    try {
      await page.locator("#endpoint-form").evaluate((form) => {
        form.requestSubmit();
        form.requestSubmit();
      });
      await expect.poll(() => writes).toBe(1);
      await expect(page.locator("#form-error")).toContainText("already in progress");
      await expect(page.getByRole("button", { name: "Apply endpoint" })).toBeDisabled();
    } finally {
      release();
    }
    await expect(page.locator("#form-error")).toHaveText("Write unavailable");
    await expect(page.getByRole("button", { name: "Apply endpoint" })).toBeEnabled();
    expect(writes).toBe(1);
    await page.unroute("**/__mockapi/api/endpoints");
    await page.getByRole("button", { name: "Apply endpoint" }).click();
    await expect(page.locator(".endpoint-row")).toContainText("Single submission");
  });

  test("restores a rejected optimistic enable toggle from authoritative state", async ({ page, request }) => {
    const { row } = await arrangeEndpoint(page, request);
    await page.route(`**/endpoints/${endpoint.id}/enabled`, (route) =>
      route.fulfill({
        status: 503,
        contentType: "application/problem+json",
        body: JSON.stringify({ detail: "Toggle unavailable" }),
      })
    );
    await row.locator(".switch input").click();
    await expect(page.locator("#toast-region .error")).toHaveText("Toggle unavailable");
    const toggle = row.getByRole("checkbox", { name: `Disable ${endpoint.name}`, exact: true });
    await expect(toggle).toBeChecked();
    await expect(toggle).toBeEnabled();
    await expect(toggle).toBeFocused();
  });
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
  await expect(page.locator("#endpoint-rows .endpoint-row")).toHaveCount(7);
  const hello = await request.get("/ex/hello");
  expect(await hello.text()).toBe('{"message":"Hello from MockAPI"}');
});
