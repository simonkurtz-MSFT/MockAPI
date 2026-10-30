import { test, expect, expectNoUnreviewedAccessibilityViolations } from "./dashboard-fixtures.js";

const hour = 60 * 60 * 1000;

async function releases(page) {
  const current = await page.locator("#dashboard-version").getAttribute("content");
  return { current, newer: `${BigInt(current.split(".")[0]) + 1n}.0.0` };
}

async function pauseClock(page) {
  await page.clock.install({ time: new Date("2026-01-01T00:00:00Z") });
  await page.clock.pauseAt(new Date("2026-01-01T00:00:01Z"));
}

test("checks at startup and hourly for releases, then stops while an update is pending @smoke", async ({ page }) => {
  const { current, newer } = await releases(page);
  let version = `${current}+another-commit`;
  let checks = 0;
  await page.route("**/__mockapi/version.txt", (route) => {
    checks += 1;
    return route.fulfill({ contentType: "text/plain", body: version });
  });
  await pauseClock(page);
  await page.reload();
  await expect.poll(() => checks).toBe(1);
  await expect(page.getByRole("button", { name: "Update dashboard", exact: true })).toBeHidden();
  await page.locator("#theme-toggle").focus();
  await page.clock.fastForward(hour - 1);
  expect(checks).toBe(1);
  version = newer;
  await page.clock.fastForward(1);
  const update = page.getByRole("button", { name: "Update dashboard", exact: true });
  await expect(update).toBeVisible();
  await expect(update).toHaveAttribute("title", `Update to MockAPI ${newer}`);
  await expect(page.locator("#dashboard-update-message")).toContainText(`MockAPI ${newer} is ready.`);
  await expect(page.locator("#dashboard-update-prompt")).toBeVisible();
  await expect(page.locator("#theme-toggle")).toBeFocused();
  await expect(update).toHaveAttribute("aria-expanded", "true");
  await page.clock.fastForward(3 * hour);
  expect(checks).toBe(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.clock.resume();
  await expectNoUnreviewedAccessibilityViolations(page, ".app-header");
  await expectNoUnreviewedAccessibilityViolations(page, "#dashboard-update-prompt");
});

test("dismissal lasts for the browser session while the header button can reopen the notice", async ({
  page,
  context,
}) => {
  const { newer } = await releases(page);
  let checks = 0;
  await context.route("**/__mockapi/version.txt", (route) => {
    checks += 1;
    return route.fulfill({ contentType: "text/plain", body: newer });
  });
  await pauseClock(page);
  await page.reload();
  await page.getByRole("button", { name: "Not now", exact: true }).click();
  const update = page.getByRole("button", { name: "Update dashboard", exact: true });
  await expect(update).toBeVisible();
  await expect(update).toBeFocused();
  await expect(update).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator("#dashboard-update-prompt")).toBeHidden();
  await page.clock.fastForward(3 * hour);
  expect(checks).toBe(1);
  await update.click();
  await expect(page.getByRole("button", { name: "Update now", exact: true })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(page.locator("#dashboard-update-prompt")).toBeHidden();
  await expect(update).toBeFocused();
  await page.reload();
  await expect(update).toBeVisible();
  await expect(page.locator("#dashboard-update-prompt")).toBeHidden();
  expect(checks).toBe(2);
  expect(await page.evaluate(() => sessionStorage.getItem("mockapi.updateDismissed"))).toBe("true");

  const newSession = await context.newPage();
  try {
    await newSession.goto("/?tutorial=skip");
    await expect(newSession.locator("#dashboard-update-prompt")).toBeVisible();
    expect(await newSession.evaluate(() => sessionStorage.getItem("mockapi.updateDismissed"))).toBeNull();
  } finally {
    await newSession.close();
  }
});

test("updating reloads the deployed dashboard without clearing browser data or changing the URL @smoke", async ({
  page,
  context,
}) => {
  const { current, newer } = await releases(page);
  let serveUpdatedHtml = false;
  await page.route("**/__mockapi/version.txt", (route) => route.fulfill({ contentType: "text/plain", body: newer }));
  const origin = new URL(page.url()).origin;
  // Root navigations are forwarded by the PWA worker, so intercept its network read at context scope.
  await context.route(
    (url) => url.origin === origin && url.pathname === "/",
    async (route) => {
      const response = await route.fetch();
      const html = await response.text();
      const body = serveUpdatedHtml
        ? html.replace(`name="mockapi-version" content="${current}"`, `name="mockapi-version" content="${newer}"`)
        : html;
      await route.fulfill({ response, body });
    }
  );
  await page.goto("/?tutorial=skip#main");
  await expect(page.locator("#dashboard-update-prompt")).toBeVisible();
  const url = page.url();
  await page.evaluate(async () => {
    localStorage.setItem("mockapi.update-test", "preserved");
    await caches.open("unrelated-application-cache");
  });
  serveUpdatedHtml = true;
  await page.getByRole("button", { name: "Update now", exact: true }).click();
  await expect(page.locator("#dashboard-version")).toHaveAttribute("content", newer);
  await expect(page.getByRole("heading", { name: /^Endpoints/ })).toBeVisible();
  await expect(page.getByRole("button", { name: "Update dashboard", exact: true })).toBeHidden();
  await expect(page.locator("#dashboard-update-prompt")).toBeHidden();
  expect(page.url()).toBe(url);
  expect(await page.evaluate(() => localStorage.getItem("mockapi.update-test"))).toBe("preserved");
  expect(await page.evaluate(() => caches.keys())).toContain("unrelated-application-cache");
});

test("failed version checks report the error and retry at the next hourly interval", async ({ page }) => {
  const { newer } = await releases(page);
  let available = false;
  let checks = 0;
  await page.route("**/__mockapi/version.txt", (route) => {
    checks += 1;
    return route.fulfill({
      status: available ? 200 : 503,
      contentType: "text/plain",
      body: available ? newer : "Unavailable",
    });
  });
  await pauseClock(page);
  await page.reload();
  await expect(page.locator(".toast.error")).toContainText("release check failed (HTTP 503)");
  await expect(page.getByRole("button", { name: "Update dashboard", exact: true })).toBeHidden();
  await page.clock.fastForward(hour - 1);
  expect(checks).toBe(1);
  available = true;
  await page.clock.fastForward(1);
  await expect(page.locator("#dashboard-update-prompt")).toBeVisible();
  expect(checks).toBe(2);
});

test("release checks also work in browsers without service-worker support", async ({ page }) => {
  const { newer } = await releases(page);
  await page.addInitScript(() => {
    delete Navigator.prototype.serviceWorker;
  });
  await page.route("**/__mockapi/version.txt", (route) => route.fulfill({ contentType: "text/plain", body: newer }));
  await page.reload();
  expect(await page.evaluate(() => "serviceWorker" in navigator)).toBe(false);
  await expect(page.getByRole("button", { name: "Update dashboard", exact: true })).toBeVisible();
  await expect(page.locator("#dashboard-update-prompt")).toBeVisible();
});
