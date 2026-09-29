import AxeBuilder from "@axe-core/playwright";
import { expect, test as base } from "@playwright/test";

export { expect } from "@playwright/test";

/** Empty configuration used to isolate dashboard scenarios. */
export const emptyDocument = {
  $schema: "../schemas/mockapi.schema.json",
  schemaVersion: "1.0",
  endpoints: [],
};

/**
 * Imports a complete configuration using the server's current revision.
 * @param {import("@playwright/test").APIRequestContext} request Test request context.
 * @param {object} document Configuration document.
 * @returns {Promise<void>} Resolves after a successful import; asserts on management errors.
 */
export async function importDocument(request, document) {
  const status = await request.get("/__mockapi/api/configuration");
  expect(status.ok(), await status.text()).toBeTruthy();
  const configuration = await status.json();
  const response = await request.put("/__mockapi/api/configuration/import", {
    headers: { "If-Match": configuration.etag },
    data: document,
  });
  expect(response.ok(), await response.text()).toBeTruthy();
}

/** Dashboard runner with a clean shared registry, empty statistics, and a ready page for every scenario. */
export const test = base.extend({
  dashboardReady: [
    async ({ page, request }, use) => {
      await expect(() => importDocument(request, emptyDocument)).toPass({
        intervals: [100, 250, 500, 1_000],
        timeout: 5_000,
      });
      const reset = await request.post("/__mockapi/api/statistics/reset");
      expect(reset.ok(), await reset.text()).toBeTruthy();
      await page.addInitScript(() => {
        if (!window.localStorage.getItem("mockapi.preferences")) {
          window.localStorage.setItem("mockapi.preferences", JSON.stringify({ version: 1, tutorialDismissed: true }));
        }
      });
      await page.goto("/");
      await expect(page.getByRole("heading", { name: /^Endpoints/ })).toBeVisible();
      await expect(page.locator("#connection-status")).toHaveText("Live");
      await use();
    },
    { auto: true },
  ],
});

/**
 * Checks the page or one region for unreviewed moderate-or-higher axe violations.
 * @param {import("@playwright/test").Page} page Dashboard page.
 * @param {string} [includeSelector] Optional region selector.
 * @returns {Promise<void>} Resolves when the accessibility assertion passes.
 */
export async function expectNoUnreviewedAccessibilityViolations(page, includeSelector) {
  const builder = new AxeBuilder({ page });
  if (includeSelector) builder.include(includeSelector);
  const results = await builder.analyze();
  const violations = results.violations.filter((violation) =>
    ["moderate", "serious", "critical"].includes(violation.impact)
  );
  expect(violations, JSON.stringify(violations, null, 2)).toEqual([]);
}

/**
 * Calculates WCAG contrast for computed CSS RGB colors.
 * @param {string} firstColor Foreground RGB color.
 * @param {string} secondColor Background RGB color.
 * @returns {number} Contrast ratio.
 */
export function contrastRatio(firstColor, secondColor) {
  const luminance = (color) => {
    const channels = color
      .match(/[\d.]+/g)
      .slice(0, 3)
      .map(Number);
    const linearChannels = channels.map((channel) => {
      const normalized = channel / 255;
      return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * linearChannels[0] + 0.7152 * linearChannels[1] + 0.0722 * linearChannels[2];
  };
  const firstLuminance = luminance(firstColor);
  const secondLuminance = luminance(secondColor);
  return (Math.max(firstLuminance, secondLuminance) + 0.05) / (Math.min(firstLuminance, secondLuminance) + 0.05);
}
