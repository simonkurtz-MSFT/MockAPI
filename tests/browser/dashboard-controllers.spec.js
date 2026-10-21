import { expect, test } from "@playwright/test";

test("header protection ignores obsolete reads and completions after disposal", async ({ page, request }) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async () => {
    const { createDashboardApiSecurity } = await import("/dashboard-api-security.js");
    window.pendingStatusReads = [];
    window.security = createDashboardApiSecurity({
      documentRoot: document,
      api: () => new Promise((resolve) => window.pendingStatusReads.push(resolve)),
      showError: () => {},
      confirm: () => true,
      copyToClipboard: async () => {},
    });
    void window.security.refresh();
    void window.security.refresh();
    window.pendingStatusReads[1]({ enabled: true, configured: true, etag: '"new"' });
  });
  const indicator = frame.locator("#header-api-security");
  await expect(indicator).toHaveText("APIs protected");
  await frame.evaluate(() => window.pendingStatusReads[0]({ enabled: false, configured: true, etag: '"obsolete"' }));
  await expect(indicator).toHaveText("APIs protected");
  await frame.evaluate(() => {
    void window.security.refresh();
    window.security.dispose();
    window.pendingStatusReads[2]({ enabled: false, configured: true, etag: '"disposed"' });
  });
  await expect(indicator).toHaveText("APIs protected");
});

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
  for (const length of [32, 36, 43, 256]) {
    const suppliedKey = "deployment_key-" + "a".repeat(length - 15);
    await frame.locator("#settings-api-key").fill(suppliedKey);
    await expect(frame.locator("#settings-api-key-error")).toBeHidden();
    expect(await frame.evaluate(() => window.security.getKey())).toBe(suppliedKey);
  }
  await frame.locator("#settings-api-key").fill("a".repeat(31));
  await expect(frame.locator("#settings-api-key-error")).toBeVisible();
  expect(await frame.evaluate(() => window.security.getKey())).toBe("");
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
  await expect(frame.locator("#header-api-security")).toHaveText("APIs unprotected");
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
  await expect(frame.locator("#header-api-security")).toHaveText("APIs protected");
  await expect(frame.locator("#settings-api-security-enabled")).toBeEnabled();
  await expect(frame.locator(".security-warning-icon")).toBeHidden();
  await frame.locator("#settings-api-security-enabled").uncheck();
  expect(await frame.evaluate(() => window.confirmations.length)).toBe(1);
  expect(await frame.evaluate(() => window.securityCalls[3].options.etag)).toBe('"5"');
  expect(await frame.evaluate(() => JSON.parse(window.securityCalls[3].options.body))).toEqual({ enabled: false });
  await frame.evaluate(() => window.pendingSecurity[1]());
  await expect(frame.locator("#settings-api-security-summary")).toHaveText("Protection off");
  await expect(frame.locator("#header-api-security")).toHaveText("APIs unprotected");
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

test("test blade ignores obsolete security checks and cancels before a pending preflight sends", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  await frame.evaluate(async (definition) => {
    const { createDashboardTestBlade } = await import("/dashboard-test-blade.js");
    window.securityChecks = [];
    window.sentRequests = 0;
    window.blade = createDashboardTestBlade({
      documentRoot: document,
      checkRequestSecurity: () => new Promise((resolve) => window.securityChecks.push(resolve)),
      createRequestController: () => ({
        send: async () => {
          window.sentRequests++;
          return { kind: "cancelled", message: "Request cancelled." };
        },
        cancel: () => {},
      }),
      copyToClipboard: async () => {},
      showError: () => {},
    });
    const trigger = document.getElementById("create-button");
    window.blade.open(definition, trigger);
    window.blade.close();
    window.blade.open(definition, trigger);
    window.securityChecks[0]("obsolete warning");
  }, endpoint);
  await expect(frame.locator("#test-security-warning")).toBeHidden();
  await frame.evaluate(() => window.securityChecks[1]("current warning"));
  await expect(frame.locator("#test-security-warning")).toHaveText("current warning");
  await frame.locator("#test-send").click();
  await expect.poll(() => frame.evaluate(() => window.securityChecks.length)).toBe(3);
  await frame.locator("#test-cancel").click();
  await frame.evaluate(() => window.securityChecks[2]("missing key"));
  await expect(frame.locator("#test-status")).toHaveText("Request cancelled.");
  await expect(frame.locator("#test-send")).toBeEnabled();
  expect(await frame.evaluate(() => window.sentRequests)).toBe(0);
  await frame.locator("#test-send").click();
  await expect.poll(() => frame.evaluate(() => window.securityChecks.length)).toBe(4);
  await frame.evaluate(() => {
    window.blade.dispose();
    window.securityChecks[3]("disposed warning");
  });
  await expect(frame.locator("#test-blade-shell")).toBeHidden();
  await expect(frame.locator("#test-security-warning")).toBeHidden();
  expect(await frame.evaluate(() => window.sentRequests)).toBe(0);
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
      checkRequestSecurity: async () => "",
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
      checkRequestSecurity: async () => "",
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
    window.table.updateStatistics({
      endpoints: [{ endpointId: definition.id, totalRequests: 9, lastRequestUtc: "2026-10-09T16:30:00Z" }],
    });
  }, endpoint);
  await expect(frame.locator("[data-endpoint-requests]")).toHaveText("9");
  await expect(frame.locator("[data-endpoint-requests]")).toHaveAttribute("title", /^Last attempt: (?!Never$).+/);
  await expect(frame.locator("[data-endpoint-requests]")).toHaveAttribute(
    "aria-label",
    /^9 attempts\. Last attempt: (?!Never$).+/
  );
  await expect(frame.locator("#selection-count")).toHaveText("1");
  await expect(frame.locator(".endpoint-group-selection")).toBeChecked();
  expect(await frame.evaluate(() => document.querySelector(".endpoint-row") === window.originalRow)).toBe(true);
  await frame.evaluate((definition) => {
    window.oldEdit = document.querySelector('.endpoint-row button[title="Edit"]');
    window.oldGroupSelection = document.querySelector(".endpoint-group-selection");
    window.table.updateEndpoints([{ ...definition, name: "Refreshed endpoint" }]);
    window.oldEdit.click();
    window.oldGroupSelection.click();
  }, endpoint);
  expect(await frame.evaluate(() => window.actions.length)).toBe(1);
  await expect(frame.locator(".endpoint-group-selection")).toBeChecked();
  await expect(frame.getByRole("checkbox", { name: "Select Refreshed endpoint", exact: true })).toBeChecked();
  await frame.locator(".switch input").focus();
  await frame.evaluate(() => window.table.setPending(true));
  await expect(frame.locator(".switch input")).toBeDisabled();
  await frame.evaluate(() => window.table.setPending(false));
  await expect(frame.locator(".switch input")).toBeFocused();
  await frame.getByRole("button", { name: "Edit", exact: true }).click();
  expect(await frame.evaluate(() => window.actions.length)).toBe(2);

  const groupControl = frame.locator("#endpoint-toggle-groups");
  await groupControl.click();
  await expect(frame.locator(".endpoint-row")).toHaveCount(0);
  await expect(groupControl).toHaveText("Expand all groups");
  await frame.evaluate((definition) => {
    window.table.updateEndpoints([
      { ...definition, name: "Refreshed endpoint" },
      { ...definition, id: "cb11041d-1ba5-4c93-86a4-6570adfc282e", name: "New group", path: "/new-group/route" },
    ]);
  }, endpoint);
  await expect(frame.locator(".endpoint-row")).toHaveCount(1);
  await expect(frame.locator(".endpoint-row")).toContainText("New group");
  await expect(groupControl).toHaveText("Collapse all groups");
  await groupControl.click();
  await expect(frame.locator(".endpoint-row")).toHaveCount(0);
  await frame.evaluate(() => window.table.updateStatistics({ endpoints: [] }));
  await expect(frame.locator(".endpoint-row")).toHaveCount(0);
  await expect(groupControl).toHaveText("Expand all groups");
  await groupControl.click();
  await expect(frame.locator(".endpoint-row")).toHaveCount(2);
  await expect(frame.getByRole("checkbox", { name: "Select Refreshed endpoint", exact: true })).toBeChecked();
  await frame.evaluate(
    (definition) => window.table.updateEndpoints([{ ...definition, name: "Refreshed endpoint" }]),
    endpoint
  );
  await expect(frame.locator(".endpoint-row")).toHaveCount(1);
  await expect(groupControl).toHaveText("Collapse all groups");

  await frame.evaluate(() => window.table.dispose());
  await frame.locator(".endpoint-group-selection").click();
  expect(await frame.evaluate(() => window.table.getSelectedEndpointIds())).toEqual([endpoint.id]);
  await groupControl.click();
  await expect(frame.locator(".endpoint-row")).toHaveCount(1);
  await frame.getByRole("button", { name: "Edit", exact: true }).click();
  await frame.locator("#bulk-enable").click();
  await frame.locator("#filter-text").fill("does not match");
  expect(await frame.evaluate(() => window.actions.length)).toBe(2);
  await expect(frame.locator(".endpoint-row")).toHaveCount(1);
});

test("statistics controller retains current chart listeners and releases replaced chart listeners", async ({
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
    window.retainedBucket = document.querySelector(".request-log-bucket-toggle") === window.originalBucket;
    window.oldColumn.click();
  });
  expect(await frame.evaluate(() => window.retainedBucket)).toBe(true);
  await expect(frame.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "true");
  await frame.locator(".request-log-bucket-toggle").click();
  await frame.evaluate(() => {
    window.panel.updateStatistics({
      ...window.statistics,
      recentMinutes: [{ ...window.statistics.recentMinutes[0], requests: 0, successResponses: 0 }],
    });
    window.oldColumn.click();
  });
  await expect(frame.locator(".request-log-bucket-toggle")).toHaveAttribute("aria-expanded", "false");
  await frame.evaluate(() => window.panel.updateStatistics(window.statistics));
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

test("statistics updates retain keyed rows and sixty minute columns, focus, tooltip, and scroll", async ({
  page,
  request,
}) => {
  const frame = await createFixture(page, request);
  const statistics = await (await request.get("/__mockapi/api/statistics")).json();
  await frame.evaluate(
    async ({ definition, statistics }) => {
      const { createDashboardStatistics } = await import("/dashboard-statistics.js");
      const { createDashboardPreferencesStore } = await import("/dashboard-preferences.js");
      const start = Date.UTC(2026, 0, 1);
      window.buckets = Array.from({ length: 60 }, (_, index) => ({
        minuteUtc: new Date(start + index * 60000).toISOString(),
        requests: index === 0 || index >= 58 ? 1 : 0,
        successResponses: index === 0 || index >= 58 ? 1 : 0,
      }));
      window.statistics = {
        ...statistics,
        totalRequests: 3,
        recentMinutes: window.buckets,
        endpoints: [{ endpointId: definition.id, totalRequests: 3, recentMinutes: window.buckets }],
        recentRequests: window.buckets.slice(58).map((bucket) => ({
          timestampUtc: bucket.minuteUtc,
          endpointId: definition.id,
          method: "GET",
          path: definition.path,
          outcome: "response",
          statusCode: 200,
          responseBytes: 1,
        })),
      };
      window.definition = definition;
      window.panel = createDashboardStatistics({
        documentRoot: document,
        preferencesStore: createDashboardPreferencesStore({ getItem: () => null, setItem() {} }),
        onReset() {},
      });
      window.panel.updateEndpoints([definition, { ...definition, id: "second", name: "Second" }]);
      window.panel.updateStatistics(window.statistics);
    },
    { definition: endpoint, statistics }
  );
  await frame.locator("#statistics-endpoint").click();
  await frame.locator("#statistics-table").click();
  expect(
    await frame.evaluate(() => {
      const table = document.querySelector("#statistics-table-view table");
      const row = table.tBodies[0].rows[0];
      const name = row.querySelector("strong");
      window.panel.updateStatistics({
        ...window.statistics,
        endpoints: [{ ...window.statistics.endpoints[0], totalRequests: 7 }],
      });
      const kept = table === document.querySelector("#statistics-table-view table") && row === table.tBodies[0].rows[0];
      window.panel.updateEndpoints([{ ...window.definition, name: "Renamed" }]);
      return (
        kept &&
        name === row.querySelector("strong") &&
        name.textContent === "Renamed" &&
        row.cells[1].textContent === "7" &&
        table.tBodies[0].rows.length === 1
      );
    })
  ).toBe(true);
  await frame.locator("#statistics-overall").click();
  expect(
    await frame.evaluate(() => {
      const row = document.querySelector("#statistics-table-view tbody tr");
      window.panel.updateStatistics(window.statistics);
      return row === document.querySelector("#statistics-table-view tbody tr");
    })
  ).toBe(true);
  await frame.locator("#statistics-graph").click();
  await frame.locator(".chart-column.interactive").first().hover();
  expect(
    await frame.evaluate(() => {
      const chart = document.querySelector("#statistics-chart");
      const columns = [...chart.querySelectorAll(".chart-column")];
      const focus = columns[58];
      const segment = focus.querySelector(".status-2xx");
      const tooltip = chart.querySelector(".chart-tooltip");
      const scroll = document.querySelector("#statistics-chart-scroll");
      focus.focus({ preventScroll: true });
      scroll.scrollLeft = 10;
      const left = scroll.scrollLeft;
      const changed = window.buckets.map((bucket, index) =>
        index === 58 ? { ...bucket, requests: 2, successResponses: 2 } : bucket
      );
      window.panel.updateStatistics({ ...window.statistics, totalRequests: 4, recentMinutes: changed });
      return (
        [...chart.querySelectorAll(".chart-column")].every((column, index) => column === columns[index]) &&
        segment === focus.querySelector(".status-2xx") &&
        segment.style.flexGrow === "2" &&
        document.activeElement === focus &&
        scroll.scrollLeft === left &&
        chart.querySelector(".chart-tooltip") === tooltip &&
        tooltip.textContent.includes("2 attempts")
      );
    })
  ).toBe(true);
  expect(
    await frame.evaluate(() => {
      const chart = document.querySelector("#statistics-chart");
      const kept = chart.querySelectorAll(".chart-column")[58];
      const rolled = [
        ...window.buckets.slice(1),
        { ...window.buckets[59], minuteUtc: "2026-01-01T01:00:00Z", requests: 0, successResponses: 0 },
      ];
      window.panel.updateStatistics({ ...window.statistics, recentMinutes: rolled });
      return (
        chart.querySelectorAll(".chart-column").length === 60 && chart.querySelectorAll(".chart-column")[57] === kept
      );
    })
  ).toBe(true);
  await frame.evaluate(() => window.panel.dispose());
});
