import { expect, test } from "@playwright/test";

test("API security controller generates once, uses captured revisions, and ignores closed completions", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async () => {
    const { createDashboardApiSecurity } = await import("/dashboard-api-security.js");
    window.securityCalls = [];
    window.securityErrors = [];
    window.confirmations = [];
    window.pendingSecurity = [];
    let current = { enabled: true, configured: false, etag: '"0"' };
    window.security = createDashboardApiSecurity({
      documentRoot: document,
      api: async (path, options) => {
        window.securityCalls.push({ path, options });
        if (!options) return current;
        return new Promise((resolve) =>
          window.pendingSecurity.push((result) => {
            current = result.status ?? result;
            resolve(result);
          })
        );
      },
      showError: (message) => window.securityErrors.push(message),
      confirm: (message) => {
        window.confirmations.push(message);
        return true;
      },
      copyToClipboard: async (text) => {
        window.copiedKey = text;
      },
    });
    document.getElementById("settings-dialog").showModal();
    await window.security.open();
  });
  await expect(frame.locator("#settings-api-security-status")).toContainText("blocked");
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Key needed");
  await expect(frame.locator("#settings-api-key-generate")).toHaveText("Generate key");
  await frame.locator("#settings-api-key").fill("invalid");
  await expect(frame.locator("#settings-api-key-error")).toBeVisible();
  await frame.locator("#settings-api-key-generate").click();
  await expect(frame.locator("#settings-api-key-generate")).toBeDisabled();
  expect(await frame.evaluate(() => window.securityCalls[1].options.etag)).toBe('"0"');
  await frame.evaluate(() =>
    window.pendingSecurity[0]({
      key: "a".repeat(43),
      status: { enabled: true, configured: true, etag: '"1"' },
    })
  );
  await expect(frame.locator("#settings-api-key-copy")).toBeEnabled();
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Key generated");
  await expect(frame.locator("#settings-api-key-generate")).toHaveText("Rotate key");
  await expect(frame.locator("#settings-api-key-error")).toBeHidden();
  expect(await frame.locator("#settings-api-key").evaluate((input) => input.validity.valid)).toBe(true);
  await frame.locator("#settings-api-key-copy").click();
  expect(await frame.evaluate(() => window.copiedKey)).toBe("a".repeat(43));
  await frame.locator("#settings-api-key-generate").click();
  expect(await frame.evaluate(() => window.confirmations.length)).toBe(1);
  await frame.evaluate(() => {
    window.security.close();
    document.getElementById("settings-dialog").close();
    window.pendingSecurity[1]({ key: "b".repeat(43), status: { enabled: true, configured: true, etag: '"2"' } });
  });
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Key generated");
  expect(await frame.evaluate(() => window.security.getKey())).toBe("a".repeat(43));
  await frame.evaluate(() => window.security.dispose());
  expect(await frame.evaluate(() => window.security.getKey())).toBe("");
});

test("API security controller reports write failures and confirms explicit opt-out", async ({ page, request }) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async () => {
    const { createDashboardApiSecurity } = await import("/dashboard-api-security.js");
    window.calls = [];
    window.confirmResult = false;
    window.security = createDashboardApiSecurity({
      documentRoot: document,
      api: async (path, options) => {
        window.calls.push({ path, options });
        if (!options) return { enabled: true, configured: true, etag: '"3"' };
        throw new Error("Security settings changed. Reload Settings.");
      },
      showError: (message) => {
        window.currentError = message;
      },
      confirm: () => window.confirmResult,
      copyToClipboard: async () => {},
    });
    document.getElementById("settings-dialog").showModal();
    await window.security.open();
  });
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection on");
  await frame.locator("#settings-api-security-enabled").click();
  await expect(frame.locator("#settings-api-security-enabled")).toBeChecked();
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection on");
  expect(await frame.evaluate(() => window.calls.length)).toBe(1);
  await frame.evaluate(() => {
    window.confirmResult = true;
  });
  await frame.locator("#settings-api-security-enabled").click();
  await expect(frame.locator("#settings-api-security-status")).toContainText("changed");
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Update failed");
  expect(await frame.evaluate(() => window.calls[1].options.etag)).toBe('"3"');
  expect(await frame.evaluate(() => JSON.parse(window.calls[1].options.body))).toEqual({ enabled: false });
  await expect(frame.locator("#settings-api-security-enabled")).toBeEnabled();
  await expect(frame.locator("#settings-api-security-enabled")).toBeChecked();
  expect(await frame.evaluate(() => window.currentError)).toBe("Security settings changed. Reload Settings.");
  await frame.locator("#settings-api-key").fill("invalid");
  await expect(frame.locator("#settings-api-key-copy")).toBeDisabled();
  await expect(frame.locator("#settings-api-key")).toHaveAttribute("aria-invalid", "true");
  await expect(frame.locator("#settings-api-key-error")).toBeVisible();
  expect(await frame.evaluate(() => window.security.getKey())).toBe("");
});

test("API security controller saves checkbox changes immediately and refreshes the saved protection", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async () => {
    const { createDashboardApiSecurity } = await import("/dashboard-api-security.js");
    window.securityCalls = [];
    window.pendingSecurity = [];
    window.confirmations = [];
    let current = { enabled: false, configured: true, etag: '"4"' };
    window.security = createDashboardApiSecurity({
      documentRoot: document,
      api: async (path, options) => {
        window.securityCalls.push({ path, options });
        if (!options) return current;
        return new Promise((resolve) =>
          window.pendingSecurity.push(() => {
            current = { ...current, enabled: JSON.parse(options.body).enabled, etag: '"5"' };
            resolve(current);
          })
        );
      },
      showError: (message) => {
        throw new Error(message);
      },
      confirm: (message) => {
        window.confirmations.push(message);
        return true;
      },
      copyToClipboard: async () => {},
    });
    document.getElementById("settings-dialog").showModal();
    await window.security.open();
  });
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection off");
  await expect(frame.locator("#settings-api-security-status")).toContainText("unauthenticated");
  await expect(frame.locator(".security-warning-icon")).toBeVisible();
  await expect(frame.locator("#settings-api-key-generate")).toHaveText("Rotate key");
  await frame.getByLabel("Require X-MockAPI-Key on mock requests").check();
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Saving");
  await expect(frame.locator("#settings-api-security-enabled")).toBeDisabled();
  await frame.locator("#settings-api-security-enabled").evaluate((input) => input.dispatchEvent(new Event("change")));
  expect(await frame.evaluate(() => window.securityCalls.length)).toBe(2);
  expect(await frame.evaluate(() => window.confirmations.length)).toBe(0);
  expect(await frame.evaluate(() => window.securityCalls[1].options.etag)).toBe('"4"');
  expect(await frame.evaluate(() => window.securityCalls[1].options.method)).toBe("PUT");
  expect(await frame.evaluate(() => JSON.parse(window.securityCalls[1].options.body))).toEqual({ enabled: true });
  await frame.evaluate(() => window.pendingSecurity[0]());
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection on");
  await expect(frame.locator("#settings-api-security-enabled")).toBeEnabled();
  await expect(frame.locator(".security-warning-icon")).toBeHidden();
  await frame.locator("#settings-api-security-enabled").uncheck();
  expect(await frame.evaluate(() => window.confirmations.length)).toBe(1);
  expect(await frame.evaluate(() => window.securityCalls[3].options.etag)).toBe('"5"');
  expect(await frame.evaluate(() => JSON.parse(window.securityCalls[3].options.body))).toEqual({ enabled: false });
  await frame.evaluate(() => window.pendingSecurity[1]());
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection off");
  await expect(frame.locator(".security-warning-icon")).toBeVisible();
  await frame.evaluate(async () => {
    window.security.close();
    await window.security.open();
  });
  await expect(frame.locator("#settings-api-security-enabled")).not.toBeChecked();
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection off");
});

const endpoint = {
  id: "9697f1e8-7f7b-455a-b9a7-699961338bc8",
  name: "Controller endpoint",
  enabled: true,
  methods: ["GET"],
  path: "/controller",
  response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "configured" },
};

async function createFixture(page, request) {
  const html = await (await request.get("/")).text();
  await page.goto("/health/live");
  await page.evaluate(async (markup) => {
    const parsed = new DOMParser().parseFromString(markup, "text/html");
    for (const script of parsed.querySelectorAll("script")) script.remove();
    const frame = document.createElement("iframe");
    frame.name = "controller-fixture";
    frame.style.cssText = "position:fixed;inset:0;width:100%;height:100%;border:0";
    const loaded = new Promise((resolve) => frame.addEventListener("load", resolve, { once: true }));
    frame.srcdoc = parsed.documentElement.outerHTML;
    document.body.replaceChildren(frame);
    await loaded;
  }, html);
  return page.frame({ name: "controller-fixture" });
}

test("API description editor owns revisions, pending controls, and late completion lifecycles", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async () => {
    const { createApiDescriptionEditor } = await import("/dashboard-api-description.js");
    window.saves = [];
    window.apiEditor = createApiDescriptionEditor({
      documentRoot: document,
      onSave: (path, description, etag, reportError) =>
        new Promise((resolve) => window.saves.push({ path, description, etag, reportError, resolve })),
    });
    window.apiEditor.open("/ex", "First", '"1"');
  });
  await frame.locator("#api-description-text").fill("Draft");
  await frame.locator("#api-description-apply").click();
  expect(await frame.evaluate(() => window.saves[0].etag)).toBe('"1"');
  await frame.evaluate(() => {
    window.apiEditor.setPending(true);
    document.getElementById("api-description-form").dispatchEvent(new Event("submit", { cancelable: true }));
  });
  await expect(frame.locator("#api-description-apply")).toBeDisabled();
  expect(await frame.evaluate(() => window.saves.length)).toBe(1);
  await frame.evaluate(() => {
    document.getElementById("api-description-dialog").close();
    window.apiEditor.open("/new", "New draft", '"2"');
    window.saves[0].reportError(new Error("Obsolete failure"));
    window.saves[0].resolve({ kind: "completed" });
    window.apiEditor.setPending(false);
  });
  await expect(frame.locator("#api-description-dialog")).toBeVisible();
  await expect(frame.locator("#api-description-text")).toHaveValue("New draft");
  await expect(frame.locator("#api-description-error")).toBeHidden();
  await frame.locator("#api-description-apply").click();
  expect(await frame.evaluate(() => window.saves[1].etag)).toBe('"2"');
  await frame.evaluate(() => {
    window.saves[1].reportError(new Error("Current failure"));
    window.saves[1].resolve({ kind: "failed" });
  });
  await expect(frame.locator("#api-description-error")).toHaveText("Current failure");
  await expect(frame.locator("#api-description-error")).toBeFocused();
  await frame.locator("#api-description-apply").click();
  await frame.evaluate(() => {
    window.apiEditor.dispose();
    window.saves[2].reportError(new Error("Disposed failure"));
    window.saves[2].resolve({ kind: "completed" });
    window.apiEditor.open("/disposed", "", '"3"');
    window.apiEditor.setPending(false);
    document.getElementById("api-description-form").dispatchEvent(new Event("submit", { cancelable: true }));
  });
  await expect(frame.locator("#api-description-dialog")).toBeHidden();
  expect(await frame.evaluate(() => window.saves.length)).toBe(3);
});

test("configuration editors close applied drafts when automatic persistence fails", async ({ page, request }) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async (definition) => {
    const { createDashboardEditorDialog } = await import("/dashboard-editor-dialog.js");
    const { createApiDescriptionEditor } = await import("/dashboard-api-description.js");
    window.editor = createDashboardEditorDialog({
      documentRoot: document,
      createId: () => definition.id,
      copyToClipboard: async () => {},
      onSave: async () => ({ kind: "applied-unsaved" }),
    });
    window.apiEditor = createApiDescriptionEditor({
      documentRoot: document,
      onSave: async () => ({ kind: "applied-unsaved" }),
    });
    window.editor.open(definition, '"1"');
  }, endpoint);
  await frame.getByRole("button", { name: "Apply endpoint" }).click();
  await expect(frame.locator("#endpoint-dialog")).toBeHidden();
  await frame.evaluate(() => window.apiEditor.open("/ex", "Active draft", '"2"'));
  await frame.locator("#api-description-apply").click();
  await expect(frame.locator("#api-description-dialog")).toBeHidden();
  await frame.evaluate(() => {
    window.editor.dispose();
    window.apiEditor.dispose();
  });
});

test("test blade owns batches and rejects completions from a closed or disposed opening", async ({ page, request }) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async (definition) => {
    const { createDashboardTestBlade } = await import("/dashboard-test-blade.js");
    window.pending = [];
    window.errors = [];
    window.cancellations = 0;
    window.blade = createDashboardTestBlade({
      documentRoot: document,
      createRequestController: () => ({
        send: (request) => new Promise((resolve) => window.pending.push({ request, resolve })),
        cancel: () => window.cancellations++,
      }),
      copyToClipboard: async () => {},
      showError: (message) => window.errors.push(message),
    });
    window.completeRequest = (index, body) =>
      window.pending[index].resolve({
        kind: "completed",
        status: 200,
        statusText: "OK",
        elapsedMilliseconds: 1,
        url: location.origin + definition.path,
        headers: [],
        body,
      });
    window.blade.open({ ...definition, requestCount: 3 }, document.getElementById("create-button"));
  }, endpoint);

  await frame.locator("#test-request-headers").fill("X-Private: do-not-retain");
  await frame.locator("#test-request-body").fill("private test body");
  await frame.locator("#test-send").click();
  await expect.poll(() => frame.evaluate(() => window.pending.length)).toBe(1);
  await frame.evaluate((definition) => {
    window.blade.close();
    window.blade.open({ ...definition, name: "New opening" }, document.getElementById("create-button"));
    window.completeRequest(0, "obsolete response");
  }, endpoint);
  await expect(frame.locator("#test-blade-title")).toHaveText("New opening");
  await expect(frame.locator("#test-response-body")).toHaveText("No response yet.");
  await expect(frame.locator("#test-request-headers")).toHaveValue("");
  await expect(frame.locator("#test-request-body")).toHaveValue("");
  await expect(frame.locator("#test-send")).toBeEnabled();
  expect(await frame.evaluate(() => window.pending.length)).toBe(1);

  await frame.locator("#test-send").click();
  await expect.poll(() => frame.evaluate(() => window.pending.length)).toBe(2);
  await frame.evaluate(() => window.completeRequest(1, "current response"));
  await expect(frame.locator("#test-response-body")).toHaveText("current response");
  await frame.locator("#test-send").click();
  await expect.poll(() => frame.evaluate(() => window.pending.length)).toBe(3);
  await frame.evaluate((definition) => {
    window.blade.dispose();
    window.completeRequest(2, "disposed response");
    window.blade.open(definition, document.getElementById("create-button"));
    document.getElementById("test-send").dispatchEvent(new Event("click"));
  }, endpoint);
  await expect(frame.locator("#test-blade-shell")).toBeHidden();
  await expect(frame.locator("#test-response-body")).toHaveText("No response yet.");
  expect(await frame.evaluate(() => window.pending.length)).toBe(3);
  expect(await frame.evaluate(() => window.cancellations)).toBe(2);
  expect(await frame.evaluate(() => document.body.classList.contains("blade-open"))).toBe(false);
  await expect(frame.locator("#create-button")).toBeFocused();
});

test("test blade reports duplicate submissions and unexpected failures without leaving controls pending", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async (definition) => {
    const { createDashboardTestBlade } = await import("/dashboard-test-blade.js");
    window.errors = [];
    window.sends = 0;
    window.blade = createDashboardTestBlade({
      documentRoot: document,
      createRequestController: () => ({
        send: () => {
          window.sends++;
          return new Promise((resolve, reject) => {
            window.rejectRequest = reject;
          });
        },
        cancel() {},
      }),
      copyToClipboard: async () => {},
      showError: (message) => window.errors.push(message),
    });
    window.blade.open(definition, document.getElementById("create-button"));
  }, endpoint);
  await frame.locator("#test-send").click();
  await frame.evaluate(() => document.getElementById("test-send").dispatchEvent(new Event("click")));
  expect(await frame.evaluate(() => window.errors)).toEqual(["A request is already in progress."]);
  expect(await frame.evaluate(() => window.sends)).toBe(1);
  await frame.evaluate(() => window.rejectRequest(new Error("Request adapter failed")));
  await expect(frame.locator("#test-status")).toHaveText("Request failed: Request adapter failed");
  await expect(frame.locator("#test-send")).toBeEnabled();
  await expect(frame.locator("#test-cancel")).toBeDisabled();
  expect(await frame.evaluate(() => window.errors)).toContain("Request adapter failed");
  await frame.evaluate(() => window.blade.dispose());
});

test("editor owns captured revisions and ignores stale saves after closing, reopening, or disposal", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async (definition) => {
    const { createDashboardEditorDialog } = await import("/dashboard-editor-dialog.js");
    window.saves = [];
    window.mountEditor = () =>
      createDashboardEditorDialog({
        documentRoot: document,
        createId: () => "d050e752-9c65-4c11-bfeb-8c7823189182",
        copyToClipboard: async () => {},
        onSave: (endpoint, draft, reportError) =>
          new Promise((resolve) => window.saves.push({ endpoint, draft, reportError, resolve })),
      });
    window.editor = window.mountEditor();
    window.editor.open(definition, '"1"');
  }, endpoint);
  await frame.locator("#field-name").fill("First draft");
  await frame.getByRole("button", { name: "Apply endpoint" }).click();
  expect(await frame.evaluate(() => window.saves[0].draft)).toEqual({ editing: true, etag: '"1"' });

  await frame.evaluate((definition) => {
    document.getElementById("endpoint-dialog").close();
    window.editor.open({ ...definition, name: "New draft" }, '"2"');
    window.saves[0].reportError(new Error("Obsolete failure"));
    window.saves[0].resolve({ kind: "completed" });
  }, endpoint);
  await expect(frame.locator("#endpoint-dialog")).toBeVisible();
  await expect(frame.locator("#field-name")).toHaveValue("New draft");
  await expect(frame.locator("#form-error")).toBeHidden();
  await frame.getByRole("button", { name: "Apply endpoint" }).click();
  expect(await frame.evaluate(() => window.saves[1].draft)).toEqual({ editing: true, etag: '"2"' });
  await frame.evaluate(() => {
    window.saves[1].reportError(new Error("Current validation failure"));
    window.saves[1].resolve({ kind: "failed" });
  });
  await expect(frame.locator("#form-error")).toHaveText("Current validation failure");
  await expect(frame.locator("#endpoint-dialog")).toBeVisible();

  await frame.getByRole("button", { name: "Apply endpoint" }).click();
  await frame.evaluate(() => {
    window.editor.dispose();
    window.saves[2].reportError(new Error("Disposed failure"));
    window.saves[2].resolve({ kind: "completed" });
    window.editor.open(null, '"3"');
    window.editor.duplicate(window.saves[2].endpoint, '"3"');
    window.editor.setPending(true);
  });
  await expect(frame.locator("#endpoint-dialog")).toBeHidden();
  const methodCount = await frame.locator("#method-options input").count();
  await frame.evaluate((definition) => {
    window.editor = window.mountEditor();
    window.editor.duplicate(definition, '"3"');
  }, endpoint);
  await expect(frame.locator("#method-options input")).toHaveCount(methodCount);
  await expect(frame.locator("#dialog-title")).toHaveText("Duplicate endpoint");
  await frame.locator("#add-header").click();
  await expect(frame.locator(".header-row")).toHaveCount(1);
  await frame.getByRole("textbox", { name: "Header name", exact: true }).fill("X-Example");
  await frame.getByRole("button", { name: "Remove header" }).click();
  await expect(frame.locator(".header-row")).toHaveCount(0);
  await frame.getByRole("button", { name: "Apply endpoint" }).click();
  expect(await frame.evaluate(() => window.saves[3].draft)).toEqual({ editing: false, etag: '"3"' });
  expect(await frame.evaluate(() => window.saves[3].endpoint)).toMatchObject({
    id: "d050e752-9c65-4c11-bfeb-8c7823189182",
    name: `${endpoint.name} copy`,
    path: `${endpoint.path}-copy`,
  });
  await frame.evaluate(() => window.saves[3].resolve({ kind: "completed" }));
  await expect(frame.locator("#endpoint-dialog")).toBeHidden();
  await frame.evaluate(() => window.editor.dispose());
});

test("endpoint table owns selection snapshots and releases controls and obsolete row handlers", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async (definition) => {
    const { createDashboardEndpointTable } = await import("/dashboard-endpoint-table.js");
    const { createDashboardPreferencesStore } = await import("/dashboard-preferences.js");
    window.actions = [];
    const record =
      (action) =>
      (...args) =>
        window.actions.push({ action, args });
    window.table = createDashboardEndpointTable({
      documentRoot: document,
      preferencesStore: createDashboardPreferencesStore({ getItem: () => null, setItem() {} }),
      onEdit: record("edit"),
      onDuplicate: record("duplicate"),
      onDelete: record("delete"),
      onTest: record("test"),
      onToggle: record("toggle"),
      onBulkOperation: record("bulk"),
      onBulkDelete: record("bulkDelete"),
    });
    window.table.updateEndpoints([definition]);
    window.originalRow = document.querySelector(".endpoint-row");
  }, endpoint);
  await frame.getByRole("checkbox", { name: `Select ${endpoint.name}`, exact: true }).check();
  await frame.locator("#bulk-enable").click();
  expect(await frame.evaluate(() => window.actions[0])).toEqual({ action: "bulk", args: ["enable", [endpoint.id]] });
  await frame.evaluate((definition) => {
    window.actions[0].args[1].push("outside-selection");
    window.table.updateStatistics({ endpoints: [{ endpointId: definition.id, totalRequests: 9 }] });
  }, endpoint);
  await expect(frame.locator("[data-endpoint-requests]")).toHaveText("9");
  await expect(frame.locator("#selection-count")).toHaveText("1");
  expect(await frame.evaluate(() => document.querySelector(".endpoint-row") === window.originalRow)).toBe(true);
  await frame.evaluate((definition) => {
    window.oldEdit = document.querySelector('.endpoint-row button[title="Edit"]');
    window.table.updateEndpoints([{ ...definition, name: "Refreshed endpoint" }]);
    window.oldEdit.click();
  }, endpoint);
  expect(await frame.evaluate(() => window.actions.length)).toBe(1);
  await expect(frame.getByRole("checkbox", { name: "Select Refreshed endpoint", exact: true })).toBeChecked();
  await frame.locator(".switch input").focus();
  await frame.evaluate(() => window.table.setPending(true));
  await expect(frame.locator(".switch input")).toBeDisabled();
  await frame.evaluate(() => window.table.setPending(false));
  await expect(frame.locator(".switch input")).toBeFocused();
  await frame.getByRole("button", { name: "Edit", exact: true }).click();
  expect(await frame.evaluate(() => window.actions.length)).toBe(2);

  await frame.evaluate(() => window.table.dispose());
  await frame.getByRole("button", { name: "Edit", exact: true }).click();
  await frame.locator("#bulk-enable").click();
  await frame.locator("#filter-text").fill("does not match");
  expect(await frame.evaluate(() => window.actions.length)).toBe(2);
  await expect(frame.locator(".endpoint-row")).toHaveCount(1);
});

test("statistics controller owns scope and bucket navigation and releases replaced chart listeners", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  const statistics = await (await request.get("/__mockapi/api/statistics")).json();
  await frame.evaluate(
    async ({ definition, statistics }) => {
      const { createDashboardStatistics } = await import("/dashboard-statistics.js");
      const { createDashboardPreferencesStore } = await import("/dashboard-preferences.js");
      const minuteUtc = new Date(Math.floor(Date.now() / 60000) * 60000).toISOString();
      const bucket = { minuteUtc, requests: 1, successResponses: 1 };
      window.statistics = {
        ...statistics,
        totalRequests: 1,
        matchedRequests: 1,
        recentMinutes: [bucket],
        endpoints: [{ endpointId: definition.id, totalRequests: 1, recentMinutes: [bucket] }],
        recentRequests: [
          {
            timestampUtc: minuteUtc,
            endpointId: definition.id,
            method: "GET",
            path: definition.path,
            outcome: "response",
            statusCode: 200,
            responseBytes: 1,
          },
        ],
      };
      window.resets = [];
      window.panel = createDashboardStatistics({
        documentRoot: document,
        preferencesStore: createDashboardPreferencesStore({ getItem: () => null, setItem() {} }),
        onReset: (endpoint) => window.resets.push(endpoint?.id ?? null),
      });
      window.panel.updateEndpoints([definition]);
      window.panel.updateStatistics(window.statistics);
    },
    { definition: endpoint, statistics }
  );
  await frame.locator("#statistics-endpoint").click();
  await frame.locator("#reset-statistics-scope").click();
  expect(await frame.evaluate(() => window.resets)).toEqual([endpoint.id]);
  await frame.locator("#statistics-overall").click();
  await frame.locator("#reset-statistics-scope").click();
  expect(await frame.evaluate(() => window.resets)).toEqual([endpoint.id, null]);
  await frame.locator(".request-log-bucket-toggle").click();
  await expect(frame.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "false");
  await frame.evaluate(() => {
    window.panel.updateStatistics(window.statistics);
    window.oldColumn = document.querySelector(".chart-column.interactive");
    window.originalBucket = document.querySelector(".request-log-bucket-toggle");
    window.panel.updateStatistics(window.statistics);
    window.oldColumn.click();
  });
  expect(
    await frame.evaluate(() => document.querySelector(".request-log-bucket-toggle") === window.originalBucket)
  ).toBe(true);
  await expect(frame.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "false");
  await frame.locator(".chart-column.interactive").click();
  await expect(frame.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "true");
  await expect(frame.locator(".request-log-bucket-toggle")).toBeFocused();
  await frame.locator(".request-log-bucket-toggle").click();
  await frame.evaluate(() => window.panel.dispose());
  await frame.locator(".chart-column.interactive").click();
  await expect(frame.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "false");
  await frame.locator("#reset-statistics-scope").click();
  expect(await frame.evaluate(() => window.resets)).toEqual([endpoint.id, null]);
});
