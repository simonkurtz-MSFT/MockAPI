import { expect, test, importDocument, expectNoUnreviewedAccessibilityViolations } from "./dashboard-fixtures.js";

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
  await expect(page.locator("#settings-api-security-status")).toContainText("Configure dashboard administrator");
  await expect(page.locator("#settings-api-security-summary")).toHaveText("Admin required");
  await expect(page.locator("#settings-api-key-generate")).toBeDisabled();
  const key = "a".repeat(43);
  await page.getByLabel("API key for dashboard tests").fill(key);
  await expectNoUnreviewedAccessibilityViolations(page, "#settings-dialog");
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await page.getByRole("button", { name: "Test", exact: true }).click();
  const sent = page.waitForRequest((candidate) => candidate.url().endsWith("/key-test"));
  await page.getByRole("button", { name: "Send request", exact: true }).click();
  expect((await sent).headers()["x-mockapi-key"]).toBe(key);
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toContain(key);
  await page.reload();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByLabel("API key for dashboard tests")).toHaveValue("");
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
    await expect(page.locator("#settings-api-security-enabled")).toBeDisabled();
    await expect(page.locator("#settings-api-security-apply")).toBeDisabled();
    await expect(page.getByRole("button", { name: "Generate key", exact: true })).toBeDisabled();
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
    await page.keyboard.press("Tab");
    await expect(keyInput).toBeFocused();
    await keyInput.fill("invalid");
    await expect(keyInput).toHaveAttribute("aria-invalid", "true");
    await expect(page.locator("#settings-api-key-error")).toHaveText("Enter a generated 43-character MockAPI key.");
    await expect(page.getByRole("button", { name: "Copy key", exact: true })).toBeDisabled();
    await keyInput.fill("a".repeat(43));
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
        copyLeft: copy.left,
        copyRight: copy.right,
        cardRight: card.right,
      };
    });
    expect(sizing.contentWidth).toBeLessThanOrEqual(sizing.availableWidth);
    expect(sizing.inputRight).toBeLessThan(sizing.copyLeft);
    expect(sizing.copyRight).toBeLessThanOrEqual(sizing.cardRight);
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
  await expect(page.getByLabel("Workspace layout")).toBeFocused();
  await expect(page.getByLabel("Workspace layout")).toBeInViewport();
  await page.getByLabel("Workspace layout").selectOption("stacked");
  await page.getByRole("button", { name: "Done", exact: true }).click();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByLabel("Workspace layout")).toHaveValue("stacked");
  await expectNoUnreviewedAccessibilityViolations(page, "#settings-dialog");
});
