import { expect, test, importDocument, expectNoUnreviewedAccessibilityViolations } from "./dashboard-fixtures.js";

test("header shows unprotected APIs without opening Settings and stays visible on narrow screens", async ({ page }) => {
  const indicator = page.locator("#header-api-security");
  await expect(indicator).toHaveText("APIs unprotected");
  await expect(indicator).toHaveAttribute("data-state", "warning");
  await expect(indicator).toHaveAttribute("title", /without X-MockAPI-Key/);
  await expect(page.locator("#settings-dialog")).toBeHidden();
  await expect(page.locator("#test-blade-shell")).toBeHidden();
  for (const theme of ["light", "dark"]) {
    await page.evaluate((value) => (document.documentElement.dataset.theme = value), theme);
    await expectNoUnreviewedAccessibilityViolations(page, ".app-header");
  }
  await page.setViewportSize({ width: 320, height: 720 });
  await expect(indicator).toBeInViewport();
  await expect(page.getByRole("button", { name: "Settings", exact: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
});

test("header refreshes protection on return and distinguishes blocked and unavailable states", async ({ page }) => {
  let status = { enabled: true, configured: true, etag: '"security"' };
  let unavailable = false;
  await page.route("**/__mockapi/api/security/", (route) =>
    route.fulfill({
      status: unavailable ? 503 : 200,
      json: unavailable ? { title: "Security unavailable", status: 503 } : status,
    })
  );
  await page.reload();
  const indicator = page.locator("#header-api-security");
  await expect(indicator).toHaveText("APIs protected");
  await expect(indicator).toHaveAttribute("data-state", "protected");
  status = { ...status, configured: false };
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(indicator).toHaveText("APIs blocked: key needed");
  status = { ...status, enabled: false };
  await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));
  await expect(indicator).toHaveText("APIs unprotected");
  unavailable = true;
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await expect(indicator).toHaveText("Protection unknown");
  await expect(indicator).toHaveAttribute("data-state", "error");
  await expect(indicator).toHaveAttribute("title", /Unable to check mock API protection/);
  unavailable = false;
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true })));
  await expect(indicator).toHaveText("APIs unprotected");
});

test("Settings keeps the mock key memory-only and attaches it to dashboard tests @smoke", async ({ page, request }) => {
  await importDocument(request, {
    schemaVersion: "1.0",
    endpoints: [
      {
        id: "fc58d3ce-f8e5-4f8a-aef8-a6e1b1bdfe8f",
        name: "Key test",
        enabled: true,
        methods: ["GET"],
        path: "/key-test",
        response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
      },
    ],
  });
  await expect(page.getByRole("row", { name: /Key test/ })).toBeVisible();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.locator("#settings-api-security-summary")).toHaveText("Protection off");
  await expect(page.locator("#settings-api-key-generate")).toBeEnabled();
  const key = "a".repeat(43);
  await page.getByLabel("API key for dashboard tests").fill(key);
  await expectNoUnreviewedAccessibilityViolations(page, "#settings-dialog");
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await page.route("**/__mockapi/api/security/", (route) =>
    route.fulfill({ json: { enabled: true, configured: true, eTag: '"test-security"' } })
  );
  await page.getByRole("button", { name: "Test", exact: true }).click();
  await expect(page.locator("#test-security-warning")).toBeHidden();
  const sent = page.waitForRequest((candidate) => candidate.url().endsWith("/key-test"));
  await page.getByRole("button", { name: "Send request", exact: true }).click();
  expect((await sent).headers()["x-mockapi-key"]).toBe(key);
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toContain(key);
  await page.reload();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByLabel("API key for dashboard tests")).toHaveValue("");
});

test("Play explains missing keys, honors explicit headers, and refreshes protection before sending", async ({
  page,
  request,
}) => {
  await importDocument(request, {
    schemaVersion: "1.0",
    endpoints: [
      {
        id: "fc58d3ce-f8e5-4f8a-aef8-a6e1b1bdfe8f",
        name: "Protected play",
        enabled: true,
        methods: ["GET"],
        path: "/protected-play",
        response: { statusCode: 403, headers: {}, contentType: "text/plain", body: "deliberate forbidden" },
      },
    ],
  });
  let protectionEnabled = true;
  await page.route("**/__mockapi/api/security/", (route) =>
    route.fulfill({ json: { enabled: protectionEnabled, configured: true, eTag: '"test-security"' } })
  );
  await page.getByRole("button", { name: "Test", exact: true }).click();
  const warning = page.locator("#test-security-warning");
  await expect(warning.locator("p")).toHaveText(
    "X-MockAPI-Key is presently required, but this test will not send a key. Requests will be rejected before the endpoint runs. Please take one of the following actions:"
  );
  await expect(warning.getByRole("listitem")).toHaveText([
    "Add X-MockAPI-Key to Request headers, or",
    "enter the existing key in Settings > Dashboard test key, or",
    "disable Require X-MockAPI-Key on mock requests in Settings.",
  ]);
  await expect(warning.locator("strong code")).toHaveText(["X-MockAPI-Key", "X-MockAPI-Key", "X-MockAPI-Key"]);
  await expect(warning).toContainText("this test will not send a key");
  await expect(warning).toContainText("Request headers");
  await expect(warning).toContainText("Settings > Dashboard test key");
  await expect(warning).toContainText("disable Require X-MockAPI-Key");
  await expectNoUnreviewedAccessibilityViolations(page, "#test-blade");

  const send = page.getByRole("button", { name: "Send request", exact: true });
  await send.click();
  await expect(page.locator("#test-response-status")).toHaveText("403 Forbidden");
  await expect(warning).toBeVisible();
  await page.locator("#test-request-headers").fill("x-mockapi-key: explicit-key");
  const sent = page.waitForRequest((candidate) => candidate.url().endsWith("/protected-play"));
  await send.click();
  expect((await sent).headers()["x-mockapi-key"]).toBe("explicit-key");
  await expect(warning).toBeHidden();
  await expect(page.locator("#test-response-body")).toHaveText("deliberate forbidden");
  await expect(send).toBeEnabled();

  await page.locator("#test-request-headers").fill("X-MockAPI-Key:");
  await send.click();
  await expect(warning).toContainText("this test will not send a key");
  await expect(send).toBeEnabled();
  protectionEnabled = false;
  await send.click();
  await expect(warning).toBeHidden();
  await expect(send).toBeEnabled();
});

test("Play reports an unavailable protection check instead of assuming protection is off", async ({
  page,
  request,
}) => {
  await importDocument(request, {
    schemaVersion: "1.0",
    endpoints: [
      {
        id: "fc58d3ce-f8e5-4f8a-aef8-a6e1b1bdfe8f",
        name: "Unavailable protection",
        enabled: true,
        methods: ["GET"],
        path: "/unavailable-protection",
        response: { statusCode: 200, headers: {}, contentType: "text/plain", body: "ok" },
      },
    ],
  });
  await page.route("**/__mockapi/api/security/", (route) =>
    route.fulfill({
      status: 503,
      json: { title: "Security status unavailable", status: 503 },
    })
  );
  await page.getByRole("button", { name: "Test", exact: true }).click();
  await expect(page.locator("#test-security-warning")).toContainText("Unable to check API-key protection");
  await page.getByRole("button", { name: "Send request", exact: true }).click();
  await expect(page.locator("#test-response-body")).toHaveText("ok");
  await expect(page.locator("#test-security-warning")).toBeVisible();
});

test("Settings closes on an outside click, restores focus, and preserves the memory-only test key", async ({
  page,
  isMobile,
}) => {
  const settingsButton = page.getByRole("button", { name: "Settings", exact: true });
  const dialog = page.getByRole("dialog", { name: "Settings", exact: true });
  const keyInput = page.getByLabel("API key for dashboard tests");
  const key = "a".repeat(43);
  await settingsButton.click();
  await keyInput.fill(key);
  const heading = dialog.getByRole("heading", { name: "Mock API security", exact: true });
  await heading.click();
  await expect(dialog).toBeVisible();
  await expect(dialog).not.toHaveAttribute("closedby", "any");
  await expect(keyInput).toHaveValue(key);

  const bounds = await dialog.boundingBox();
  expect(bounds.x).toBeGreaterThan(1);
  expect(bounds.y).toBeGreaterThan(1);
  const headingBounds = await heading.boundingBox();
  await page.mouse.move(headingBounds.x + headingBounds.width / 2, headingBounds.y + headingBounds.height / 2);
  await page.mouse.down();
  await page.mouse.move(1, 1);
  await page.mouse.up();
  await expect(dialog).toBeVisible();

  await page.mouse.move(1, 1);
  await page.mouse.down();
  await page.mouse.move(headingBounds.x + headingBounds.width / 2, headingBounds.y + headingBounds.height / 2);
  await page.mouse.up();
  await expect(dialog).toBeVisible();
  await page.mouse.click(bounds.x + 2, bounds.y + 2);
  await expect(dialog).toBeVisible();

  if (isMobile) await page.touchscreen.tap(1, 1);
  else await page.mouse.click(1, 1);
  await expect(dialog).toBeHidden();
  await expect(settingsButton).toBeFocused();
  await expect(page.locator("#settings-api-key")).toHaveValue("");
  await settingsButton.click();
  await expect(keyInput).toHaveValue(key);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(settingsButton).toBeFocused();
});

for (const theme of ["light", "dark"]) {
  test(`Security Settings has compact controls and accessible sections in ${theme} theme`, async ({ page }) => {
    await page.evaluate((selectedTheme) => {
      document.documentElement.dataset.theme = selectedTheme;
    }, theme);
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    await expect(page.getByRole("button", { name: "Close settings", exact: true })).toBeFocused();
    await expect(page.getByRole("heading", { name: "Mock API security", exact: true })).toBeInViewport();
    await expect(page.getByRole("heading", { name: "Request protection", exact: true })).toBeVisible();
    await expect(page.locator("#settings-api-security-enabled")).toBeEnabled();
    await expect(page.getByRole("button", { name: "Apply protection setting", exact: true })).toHaveCount(0);
    await expect(page.locator(".security-warning-icon")).toBeVisible();
    await expect(page.getByRole("button", { name: "Generate key", exact: true })).toBeEnabled();
    await expect(page.getByRole("button", { name: "Copy key", exact: true })).toBeDisabled();
    const dimensions = await page.locator("#settings-api-security-enabled").evaluate((input) => {
      const checkbox = input.getBoundingClientRect();
      const label = input.closest("label").getBoundingClientRect();
      return { checkboxWidth: checkbox.width, checkboxHeight: checkbox.height, labelHeight: label.height };
    });
    expect(dimensions.checkboxWidth).toBe(20);
    expect(dimensions.checkboxHeight).toBe(20);
    expect(dimensions.labelHeight).toBeGreaterThanOrEqual(44);

    const keyInput = page.getByLabel("API key for dashboard tests");
    await expect(keyInput).toHaveAttribute("maxlength", "43");
    await page.keyboard.press("Tab");
    await expect(page.locator("#settings-api-security-enabled")).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(keyInput).toBeFocused();
    await keyInput.fill("invalid");
    await expect(keyInput).toHaveAttribute("aria-invalid", "true");
    await expect(page.locator("#settings-api-key-error")).toHaveText("Enter a generated 43-character MockAPI key.");
    await expect(page.getByRole("button", { name: "Copy key", exact: true })).toBeDisabled();
    await keyInput.fill("a".repeat(43));
    await keyInput.press("End");
    await keyInput.pressSequentially("b");
    await expect(keyInput).toHaveValue("a".repeat(43));
    await expect(keyInput).toHaveAttribute("aria-invalid", "false");
    await expect(page.locator("#settings-api-key-error")).toBeHidden();
    await expect(page.getByRole("button", { name: "Copy key", exact: true })).toBeEnabled();
    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "Copy key", exact: true })).toBeFocused();
    const sizing = await page.locator(".settings-body").evaluate((body) => {
      const card = body.querySelector(".security-key-control").getBoundingClientRect();
      const input = body.querySelector("#settings-api-key").getBoundingClientRect();
      const copy = body.querySelector("#settings-api-key-copy").getBoundingClientRect();
      return {
        contentWidth: body.scrollWidth,
        availableWidth: body.clientWidth,
        inputRight: input.right,
        inputWidth: input.width,
        copyLeft: copy.left,
        copyWidth: copy.width,
        inputTop: input.top,
        copyTop: copy.top,
        copyRight: copy.right,
        cardRight: card.right,
      };
    });
    expect(sizing.contentWidth).toBeLessThanOrEqual(sizing.availableWidth);
    expect(sizing.inputRight).toBeLessThan(sizing.copyLeft);
    expect(sizing.inputWidth).toBeLessThanOrEqual(460);
    expect(sizing.copyWidth).toBe(36);
    expect(Math.abs(sizing.inputTop - sizing.copyTop)).toBeLessThanOrEqual(4);
    expect(sizing.copyRight).toBeLessThanOrEqual(sizing.cardRight);
    await expect(page.locator("#settings-api-key-copy svg")).toBeVisible();
    await expectNoUnreviewedAccessibilityViolations(page, "#settings-dialog");
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await expect(page.getByRole("button", { name: "Settings", exact: true })).toBeFocused();
  });
}

test("Security Settings fits the minimum supported viewport and keeps preferences reachable", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 640 });
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Mock API security", exact: true })).toBeInViewport();
  const bounds = await page.locator("#settings-dialog").boundingBox();
  expect(bounds.x).toBeGreaterThanOrEqual(0);
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(320);
  expect(bounds.y).toBeGreaterThanOrEqual(0);
  expect(bounds.y + bounds.height).toBeLessThanOrEqual(640);
  const overflow = await page.locator(".settings-body").evaluate((body) => body.scrollWidth - body.clientWidth);
  expect(overflow).toBe(0);
  await page.keyboard.press("Tab");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Workspace layout")).toBeFocused();
  await expect(page.getByLabel("Workspace layout")).toBeInViewport();
  await page.getByLabel("Workspace layout").selectOption("stacked");
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByLabel("Workspace layout")).toHaveValue("stacked");
  await page.getByLabel("API key for dashboard tests").fill("a".repeat(43));
  const keyControlBounds = await page.locator(".security-key-control").evaluate((control) => {
    const input = control.querySelector("input").getBoundingClientRect();
    const copy = control.querySelector("button").getBoundingClientRect();
    return {
      inputRight: input.right,
      inputCenterY: input.top + input.height / 2,
      copyLeft: copy.left,
      copyCenterY: copy.top + copy.height / 2,
      copyWidth: copy.width,
    };
  });
  expect(keyControlBounds.copyLeft).toBeGreaterThan(keyControlBounds.inputRight);
  expect(keyControlBounds.copyCenterY).toBe(keyControlBounds.inputCenterY);
  expect(keyControlBounds.copyWidth).toBe(36);
  await expect(page.getByRole("button", { name: "Copy key", exact: true })).toBeEnabled();
  await expectNoUnreviewedAccessibilityViolations(page, "#settings-dialog");
});
