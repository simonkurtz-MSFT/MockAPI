import { expect, test, expectNoUnreviewedAccessibilityViolations, contrastRatio } from "./dashboard-fixtures.js";

test("@smoke displays the glassy Mock API logo and favicon", async ({ page, request }) => {
  const brandMark = page.locator(".brand-mark");
  await expect(brandMark).toHaveAttribute("src", /\/brand-mark\.svg\?v=[A-F0-9]{64}/);
  await expect(brandMark).toHaveAttribute("alt", "");
  await expect(page.locator('link[rel="icon"]')).toHaveAttribute("href", /\/favicon\.svg\?v=[A-F0-9]{64}/);
  for (const theme of ["light", "dark"]) {
    await page.evaluate((value) => document.documentElement.setAttribute("data-theme", value), theme);
    await expect(brandMark).toBeVisible();
    await expect
      .poll(() =>
        brandMark.evaluate((image) => ({
          loaded: image.complete && image.naturalWidth > 0,
          width: image.getBoundingClientRect().width,
          height: image.getBoundingClientRect().height,
        }))
      )
      .toEqual({ loaded: true, width: 42, height: 34 });
  }
  const brandMarkUrl = await brandMark.getAttribute("src");
  const faviconUrl = await page.locator('link[rel="icon"]').getAttribute("href");
  for (const asset of [brandMarkUrl, faviconUrl, "/favicon.ico"]) {
    const response = await request.get(asset);
    expect(response.ok()).toBeTruthy();
    expect(response.headers()["content-type"]).toContain("image/svg+xml");
    const label = await page.evaluate(
      (svg) => {
        const document = new DOMParser().parseFromString(svg, "image/svg+xml");
        return {
          valid: document.querySelector("parsererror") === null,
          text: document.querySelector("text")?.textContent,
        };
      },
      await response.text()
    );
    expect(label).toEqual({ valid: true, text: "Mock" });
  }
});

test("@smoke explains how MockAPI works and starts endpoint creation", async ({ page }) => {
  for (const name of ["Load", "Save", "Create"]) {
    await expect(page.getByRole("group", { name })).toBeVisible();
  }
  const commandButtonWidths = await page
    .locator(".command-actions .button")
    .evaluateAll((buttons) => buttons.map((button) => button.getBoundingClientRect().width));
  expect(new Set(commandButtonWidths).size).toBe(1);

  await page.getByRole("button", { name: "Help", exact: true }).click();
  const trigger = page.getByRole("menuitem", { name: "How MockAPI works" });
  await trigger.click();

  const dialog = page.getByRole("dialog", { name: "How MockAPI works" });
  await expect(dialog).toBeVisible();
  await expect
    .poll(() =>
      dialog.evaluate((element) => {
        const isDarkTheme = document.documentElement.dataset.theme === "dark";
        const expectedColor = isDarkTheme ? "rgba(0, 0, 0, 0.56)" : "rgba(5, 16, 30, 0.48)";
        return getComputedStyle(element, "::backdrop").backgroundColor === expectedColor;
      })
    )
    .toBe(true);
  const viewport = page.viewportSize();
  const dialogBounds = await dialog.boundingBox();
  expect(dialogBounds.x).toBeGreaterThanOrEqual(0);
  expect(dialogBounds.y).toBeGreaterThanOrEqual(0);
  expect(dialogBounds.x + dialogBounds.width).toBeLessThanOrEqual(viewport.width);
  expect(dialogBounds.y + dialogBounds.height).toBeLessThanOrEqual(viewport.height);
  if (viewport.width >= 768) {
    const introLineCount = await dialog.locator(".how-it-works-intro").evaluate((intro) => {
      const range = document.createRange();
      range.selectNodeContents(intro);
      return range.getClientRects().length;
    });
    expect(introLineCount).toBe(1);
  }
  await expect(dialog.getByRole("heading", { name: "Define" })).toBeVisible();
  await expect(dialog.getByRole("heading", { name: "Match" })).toBeVisible();
  await expect(dialog.getByText("Valid changes become active atomically, with no restart.")).toBeVisible();
  await expectNoUnreviewedAccessibilityViolations(page);

  await dialog.getByRole("button", { name: "Close how MockAPI works" }).click();
  await expect(page.getByRole("button", { name: "Help", exact: true })).toBeFocused();

  await page.getByRole("button", { name: "Help", exact: true }).click();
  await trigger.click();
  await dialog.getByRole("button", { name: "New endpoint" }).click();
  await expect(page.getByRole("dialog", { name: "New endpoint" })).toBeVisible();
});

test("starts, dismisses, persists, and repeats the dashboard tour", async ({ browser }) => {
  const freshPage = await browser.newPage();
  await freshPage.goto("/");

  const tour = freshPage.locator("#tutorial-card");
  await expect(tour).toBeVisible();
  await expect(tour).toHaveRole("dialog");
  await expect(tour.getByText("Step 1 of 4")).toBeVisible();
  await expect(freshPage.locator(".command-bar")).toHaveClass(/tutorial-highlight/);
  await expect
    .poll(() =>
      freshPage.evaluate(() => {
        const isDarkTheme = document.documentElement.dataset.theme === "dark";
        const expectedColor = isDarkTheme ? "rgba(0, 0, 0, 0.56)" : "rgba(5, 16, 30, 0.48)";
        return getComputedStyle(document.querySelector("#tutorial-backdrop")).backgroundColor === expectedColor;
      })
    )
    .toBe(true);

  for (const title of ["Watch response activity", "Inspect recent requests", "Manage endpoints"]) {
    await tour.getByRole("button", { name: "Next" }).click();
    await expect(tour.getByRole("heading", { name: title })).toBeVisible();
  }
  await tour.getByRole("button", { name: "Finish" }).click();
  await expect(tour).toBeHidden();
  await expect
    .poll(() =>
      freshPage.evaluate(() => JSON.parse(window.localStorage.getItem("mockapi.preferences")).tutorialDismissed)
    )
    .toBe(true);

  await freshPage.reload();
  await expect(tour).toBeHidden();
  await freshPage.getByRole("button", { name: "Help", exact: true }).click();
  await freshPage.getByRole("menuitem", { name: "Take dashboard tour" }).click();
  await expect(tour).toBeVisible();
  await tour.getByRole("button", { name: "Dismiss dashboard tour" }).click();
  await freshPage.close();
});

test("@smoke local launch tutorial choices override browser history without resetting preferences", async ({
  browser,
}) => {
  const freshPage = await browser.newPage();
  try {
    await freshPage.goto("/?tutorial=skip");
    await expect(freshPage.locator("#connection-status")).toContainText("Live");
    await expect(freshPage.locator("#tutorial-card")).toBeHidden();
    const preferencesBefore = await freshPage.evaluate(() => window.localStorage.getItem("mockapi.preferences"));
    expect(preferencesBefore === null || !JSON.parse(preferencesBefore).tutorialDismissed).toBe(true);

    await freshPage.goto("/");
    await expect(freshPage.locator("#tutorial-card")).toBeVisible();
    await freshPage.getByRole("button", { name: "Dismiss dashboard tour" }).click();
    const dismissedPreferences = await freshPage.evaluate(() => window.localStorage.getItem("mockapi.preferences"));
    expect(JSON.parse(dismissedPreferences).tutorialDismissed).toBe(true);

    await freshPage.goto("/?tutorial=show");
    await expect(freshPage.locator("#tutorial-card")).toBeVisible();
    expect(await freshPage.evaluate(() => window.localStorage.getItem("mockapi.preferences"))).toBe(
      dismissedPreferences
    );
    await freshPage.getByRole("button", { name: "Dismiss dashboard tour" }).click();

    await freshPage.goto("/");
    await expect(freshPage.locator("#connection-status")).toContainText("Live");
    await expect(freshPage.locator("#tutorial-card")).toBeHidden();
  } finally {
    await freshPage.close();
  }
});

test("keeps interactive color states accessible across themes", async ({ page }) => {
  for (const theme of ["light", "dark"]) {
    await page.goto(`/?scoutTheme=${theme}`);
    const createButton = page.getByRole("button", { name: "New endpoint" });
    await createButton.hover();
    await createButton.focus();
    await createButton.click();
    await page.locator('#method-options input[value="GET"]').check();
    await page.locator("#field-behavior").selectOption("abortConnection");
    await expect(page.locator("#field-status")).toBeDisabled();
    await expectNoUnreviewedAccessibilityViolations(page);
    await page.getByRole("button", { name: "Close", exact: true }).click();
  }

  await page.emulateMedia({ forcedColors: "active" });
  await page.goto("/?scoutTheme=light");
  await page.getByRole("button", { name: "New endpoint" }).click();
  await expectNoUnreviewedAccessibilityViolations(page);
});

test("shows keyboard focus on endpoint toggles across color modes", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();

  for (const theme of ["light", "dark"]) {
    await page.goto(`/?scoutTheme=${theme}`);
    const toggle = page.getByRole("checkbox", { name: "Disable Check Cloud Cruiser wait times" });
    await toggle.focus();
    await expect(toggle).toBeFocused();
    const visibleSwitch = toggle.locator("+ span");
    await expect(visibleSwitch).toHaveCSS("outline-style", "solid");
    await expect(visibleSwitch).toHaveCSS("outline-width", "3px");
    const colors = await visibleSwitch.evaluate((element) => {
      const colorProbe = document.createElement("span");
      colorProbe.style.color = "var(--cp-accent)";
      colorProbe.style.backgroundColor = "var(--cp-surface)";
      document.body.append(colorProbe);
      const probeStyles = getComputedStyle(colorProbe);
      const accent = probeStyles.color;
      const surface = probeStyles.backgroundColor;
      colorProbe.style.backgroundColor = "var(--cp-surface-soft)";
      const surfaceSoft = getComputedStyle(colorProbe).backgroundColor;
      colorProbe.remove();
      return {
        accent,
        outline: getComputedStyle(element).outlineColor,
        surface,
        surfaceSoft,
      };
    });
    expect(colors.outline).toBe(colors.accent);
    expect(contrastRatio(colors.outline, colors.surface)).toBeGreaterThanOrEqual(3);
    expect(contrastRatio(colors.outline, colors.surfaceSoft)).toBeGreaterThanOrEqual(3);
  }

  await page.emulateMedia({ forcedColors: "active" });
  await page.goto("/?scoutTheme=light");
  const toggle = page.getByRole("checkbox", { name: "Disable Check Cloud Cruiser wait times" });
  await toggle.focus();
  await expect(toggle).toBeFocused();
  await expect(toggle.locator("+ span")).toHaveCSS("outline-style", "solid");
  await expect(toggle.locator("+ span")).toHaveCSS("outline-width", "3px");
});

test("persists the selected color theme across reloads", async ({ page }) => {
  await page.evaluate(() => {
    window.localStorage.removeItem("mockapi.preferences");
    window.localStorage.removeItem("mockapi.theme");
  });
  await page.goto("/?scoutTheme=light");
  await page.getByRole("button", { name: "Toggle color theme" }).click();

  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect
    .poll(() => page.evaluate(() => JSON.parse(window.localStorage.getItem("mockapi.preferences")).theme))
    .toBe("dark");

  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  await page.getByRole("button", { name: "Toggle color theme" }).click();
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
});

test("applies and persists KJ UI Style hard corners", async ({ page }) => {
  await page.getByRole("button", { name: "Settings" }).click();
  const settings = page.getByRole("dialog", { name: "Settings" });
  const kjUiStyle = settings.getByRole("checkbox", { name: "KJ UI Style" });

  await expect(kjUiStyle).not.toBeChecked();
  await kjUiStyle.check();
  await expect(page.locator("html")).toHaveAttribute("data-kj-ui-style", "true");
  await expect(settings).toHaveCSS("border-radius", "0px");
  await expect(settings.getByRole("button", { name: "Done" })).toHaveCSS("border-radius", "0px");
  await settings.getByRole("button", { name: "Done" }).click();

  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kj-ui-style", "true");
  await expect(page.getByRole("button", { name: "Settings" })).toHaveCSS("border-radius", "0px");
  await expect
    .poll(() => page.evaluate(() => JSON.parse(window.localStorage.getItem("mockapi.preferences")).kjUiStyle))
    .toBe(true);
});

test("persists dashboard view preferences across reloads", async ({ page }) => {
  await page.getByRole("button", { name: "Load examples" }).first().click();
  await expect(page.getByLabel("Rows per page")).toHaveCount(0);
  await page.getByRole("button", { name: "Table", exact: true }).click();
  await page.getByRole("button", { name: "Collapse statistics" }).click();

  await page.reload();

  await expect(page.getByLabel("Rows per page")).toHaveCount(0);
  await expect(page.locator("#statistics-table")).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("#statistics-table-view")).toBeHidden();
  await expect(page.getByRole("button", { name: "Expand statistics" })).toHaveAttribute("aria-expanded", "false");

  await page.getByRole("button", { name: "Expand statistics" }).click();
  await expect(page.locator("#statistics-table-view")).toBeVisible();
  await expect(page.locator("#statistics-graph-view")).toBeHidden();
});
