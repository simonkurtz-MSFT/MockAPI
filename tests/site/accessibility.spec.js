const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

test.beforeEach(async ({ page }) => {
  await page.route("https://www.googletagmanager.com/gtm.js?*", (route) =>
    route.fulfill({ contentType: "application/javascript", body: "" })
  );
  await page.route("https://www.googletagmanager.com/ns.html?*", (route) =>
    route.fulfill({ contentType: "text/html", body: "" })
  );
});

test("uniform header, styled links and arrow controls meet contrast and keyboard requirements in both themes", async ({
  page,
}) => {
  test.setTimeout(60_000);
  const { contrastRatio } = await import("../browser/dashboard-fixtures.js");
  await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
  await page.goto("./");
  const theme = page.getByRole("button", { name: "Toggle color theme" });
  const previous = page.getByRole("button", { name: "Previous screenshot" });
  const next = page.getByRole("button", { name: "Next screenshot" });
  const navigation = page.getByRole("navigation", { name: "Main navigation" });
  const inlineLink = page.getByRole("link", { name: "MIT licensed" });
  const controls = [previous, next, ...(await navigation.getByRole("link").all())];

  await expect(previous).toHaveText("");
  await expect(next).toHaveText("");
  await expect(previous.locator("svg")).toHaveAttribute("aria-hidden", "true");
  await expect(next.locator("svg")).toHaveAttribute("aria-hidden", "true");
  await expect(previous.locator("path")).toHaveAttribute("d", "m15 18-6-6 6-6");
  await expect(next.locator("path")).toHaveAttribute("d", "m9 6 6 6-6 6");

  for (const mode of ["light", "dark"]) {
    if ((await page.locator("html").getAttribute("data-theme")) !== mode) await theme.click();
    const background = await page.locator("html").evaluate((root) => getComputedStyle(root).backgroundColor);
    await expect(page.locator(".site-header")).toHaveCSS("background-color", background);

    await previous.focus();
    await page.keyboard.press("Space");
    await expect(page.locator("#gallery-status")).toHaveText("Screenshot 3 of 3: Request log");
    await next.focus();
    await page.keyboard.press("Enter");
    await expect(page.locator("#gallery-status")).toHaveText("Screenshot 1 of 3: Dashboard overview");

    for (const control of [...controls, inlineLink, theme]) {
      await control.scrollIntoViewIfNeeded();
      await page.mouse.move(0, 0);
      const colors = await control.evaluate((element) => {
        const style = getComputedStyle(element);
        let background = style.backgroundColor;
        for (
          let parent = element.parentElement;
          background === "rgba(0, 0, 0, 0)" && parent;
          parent = parent.parentElement
        ) {
          background = getComputedStyle(parent).backgroundColor;
        }
        return { text: style.color, border: style.borderTopColor, background };
      });
      expect(contrastRatio(colors.text, colors.background)).toBeGreaterThanOrEqual(4.5);
      if (control !== inlineLink) {
        expect(contrastRatio(colors.border, colors.background)).toBeGreaterThanOrEqual(3);
        const size = await control.boundingBox();
        expect(size.width).toBeGreaterThanOrEqual(control === theme ? 24 : 44);
        expect(size.height).toBeGreaterThanOrEqual(control === theme ? 24 : 44);
      }
      await page.keyboard.press("Tab");
      await control.focus();
      await expect(control).toBeFocused();
      expect(
        await control.evaluate((element) => {
          const bounds = element.getBoundingClientRect();
          const centerX = bounds.left + bounds.width / 2;
          const centerY = bounds.top + bounds.height / 2;
          return (
            centerX >= 0 &&
            centerX < innerWidth &&
            centerY >= 0 &&
            centerY < innerHeight &&
            element.contains(document.elementFromPoint(centerX, centerY))
          );
        })
      ).toBe(true);
      const focus = await control.evaluate((element) => {
        const style = getComputedStyle(element);
        return { color: style.outlineColor, width: parseFloat(style.outlineWidth), style: style.outlineStyle };
      });
      expect(focus.style).toBe("solid");
      expect(focus.width).toBeGreaterThanOrEqual(2);
      expect(contrastRatio(focus.color, background)).toBeGreaterThanOrEqual(3);
      await control.hover();
      const hovered = await control.evaluate((element) => {
        const style = getComputedStyle(element);
        let background = style.backgroundColor;
        for (
          let parent = element.parentElement;
          background === "rgba(0, 0, 0, 0)" && parent;
          parent = parent.parentElement
        ) {
          background = getComputedStyle(parent).backgroundColor;
        }
        return { text: style.color, background };
      });
      expect(contrastRatio(hovered.text, hovered.background)).toBeGreaterThanOrEqual(4.5);
    }
    await expect(inlineLink).toHaveCSS("text-decoration-line", "underline");
    const selected = page.locator('.gallery-choices [aria-pressed="true"]');
    await expect(selected).toHaveCSS("text-decoration-line", "underline");
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations).toEqual([]);
  }
});

test("text resizing, reflow, reduced motion and forced colors retain usable controls", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto("./");
  await page.addStyleTag({ content: ":root { font-size: 200%; }" });
  await expect(page.locator("html")).toHaveCSS("font-size", "32px");
  const next = page.getByRole("button", { name: "Next screenshot" });
  const choices = page.getByRole("group", { name: "Choose a dashboard screenshot" });
  for (const mode of ["light", "dark"]) {
    if ((await page.locator("html").getAttribute("data-theme")) !== mode) {
      await page.getByRole("button", { name: "Toggle color theme" }).click();
    }
    for (const name of ["Overview", "Endpoints", "Request log"]) {
      await choices.getByRole("button", { name, exact: true }).click();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await expect(next).toBeVisible();
    }
    await next.hover();
    await expect(next).toHaveCSS("transition-duration", "0s");
    await expect(next).toHaveCSS("transform", "none");
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations).toEqual([]);
  }
  await page.emulateMedia({ forcedColors: "active" });
  await choices.getByRole("button", { name: "Overview", exact: true }).click();
  const selected = choices.getByRole("button", { name: "Overview", exact: true });
  await expect(selected).toHaveAttribute("aria-pressed", "true");
  await expect(selected).toHaveCSS("text-decoration-line", "underline");
  await next.focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#gallery-status")).toHaveText("Screenshot 2 of 3: Example endpoints");
  await expect(next).toHaveCSS("box-shadow", "none");
  await expect(next).toHaveCSS("transition-duration", "0s");
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations).toEqual([]);
});

test("enlarged text reflows with wider font metrics and letter spacing without clipping content", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto("./");
  // A wider fallback exposes long-word overflow even on platforms with narrower system fonts.
  await page.addStyleTag({
    content: ":root { font-size: 200%; font-family: Verdana, sans-serif; letter-spacing: 0.12em; }",
  });
  await expect(page.locator("html")).toHaveCSS("font-size", "32px");
  const choices = page.getByRole("group", { name: "Choose a dashboard screenshot" });
  for (const mode of ["light", "dark"]) {
    if ((await page.locator("html").getAttribute("data-theme")) !== mode) {
      await page.getByRole("button", { name: "Toggle color theme" }).click();
    }
    for (const name of ["Overview", "Endpoints", "Request log"]) {
      await choices.getByRole("button", { name, exact: true }).click();
      const dimensions = await page.evaluate(() => ({
        viewport: innerWidth,
        content: document.documentElement.scrollWidth,
      }));
      expect(dimensions.content).toBeLessThanOrEqual(dimensions.viewport);
      await expect(page.locator("body")).toHaveCSS("overflow-x", "visible");
      await expect(page.locator("main")).toHaveCSS("overflow-x", "visible");
    }
    for (const selector of [".badge", ".first-response", ".card"]) {
      const widths = await page
        .locator(selector)
        .evaluateAll((elements) =>
          elements.map((element) => ({ content: element.scrollWidth, available: element.clientWidth }))
        );
      for (const width of widths) expect(width.content).toBeLessThanOrEqual(width.available);
    }
  }
});
