const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

test("quick starts, assets and accessibility work under the Pages repository prefix", async ({ page }) => {
  const failures = [];
  page.on("pageerror", (error) => failures.push(error.message));
  page.on("requestfailed", (request) => failures.push(request.url()));
  page.on("response", (response) => {
    if (response.status() >= 400) failures.push(response.url());
  });
  await page.goto("./");
  await expect(page).toHaveTitle("MockAPI | Real HTTP. Predictable responses.");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Predictable responses.");
  await page.getByRole("link", { name: "Start mocking", exact: true }).click();
  await expect(page).toHaveURL(/#start$/);
  await expect(page.getByRole("link", { name: "Create a codespace" })).toHaveAttribute(
    "href",
    "https://codespaces.new/simonkurtz-MSFT/MockAPI"
  );
  expect(
    await page.locator("img").evaluateAll((images) => images.every((image) => image.complete && image.naturalWidth > 0))
  ).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations).toEqual([]);
  expect(failures).toEqual([]);
});

test("keyboard users can skip navigation and all local anchors resolve", async ({ page }) => {
  await page.goto("./");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("link", { name: "Skip to content" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#main$/);
  expect(
    await page
      .locator('a[href^="#"]')
      .evaluateAll((links) => links.every((link) => document.getElementById(link.hash.slice(1))))
  ).toBe(true);
});
