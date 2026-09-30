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
