import { test, expect, expectNoUnreviewedAccessibilityViolations } from "./dashboard-fixtures.js";

test("PWA manifest and icons support standalone installation @smoke", async ({ page, browser }, testInfo) => {
  const manifestLink = page.locator('link[rel="manifest"]');
  await expect(manifestLink).toHaveAttribute("crossorigin", "use-credentials");
  const manifest = await page.evaluate(async () => {
    const response = await fetch(document.querySelector('link[rel="manifest"]').href);
    if (!response.ok) throw new Error(`Manifest failed: ${response.status}`);
    return response.json();
  });
  expect(manifest).toMatchObject({
    id: "/",
    name: "MockAPI Console",
    start_url: "/",
    scope: "/",
    display: "standalone",
  });
  expect(manifest.icons.map((icon) => icon.sizes)).toEqual(["192x192", "512x512"]);
  for (const icon of manifest.icons) {
    const dimensions = await page.evaluate(async (src) => {
      const image = new Image();
      image.src = src;
      await image.decode();
      return `${image.naturalWidth}x${image.naturalHeight}`;
    }, icon.src);
    expect(dimensions).toBe(icon.sizes);
  }
  // Ordinary Playwright contexts are incognito, where Chromium deliberately disables installation.
  // An empty user-data directory creates an isolated temporary profile that Playwright removes on close.
  const installationContext = await browser.browserType().launchPersistentContext("", {
    channel: testInfo.project.use.channel,
    headless: true,
  });
  try {
    const installationPage = await installationContext.newPage();
    await installationPage.goto(page.url());
    await installationPage.evaluate(() => navigator.serviceWorker.ready.then(() => undefined));
    const session = await installationContext.newCDPSession(installationPage);
    await expect(async () => {
      const { installabilityErrors } = await session.send("Page.getInstallabilityErrors");
      expect(installabilityErrors).toEqual([]);
    }).toPass({ timeout: 10_000 });
  } finally {
    await installationContext.close();
  }
});

test("PWA provides a safe offline page and reconnects to live data @smoke", async ({ page, context }) => {
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
    if (navigator.serviceWorker.controller) return;
    await new Promise((resolve) =>
      navigator.serviceWorker.addEventListener("controllerchange", resolve, { once: true })
    );
  });
  const registration = await page.evaluate(async () => {
    const worker = await navigator.serviceWorker.getRegistration();
    return { scope: worker.scope, script: worker.active.scriptURL, cache: worker.updateViaCache };
  });
  expect(registration.scope).toBe(new URL("/", page.url()).href);
  expect(registration.script).toBe(new URL("/__mockapi/service-worker.js", page.url()).href);
  expect(registration.cache).toBe("none");

  await context.setOffline(true);
  try {
    const response = await page.goto("/?tutorial=show");
    expect(response.status()).toBe(503);
    await expect(page.getByRole("heading", { name: "Connection unavailable" })).toBeVisible();
    await expect(page.getByText("Changes are not queued or replayed.", { exact: false })).toBeVisible();
    await expect(page.getByRole("button", { name: "New endpoint" })).toHaveCount(0);
    const storage = await page.evaluate(async () => ({
      caches: await caches.keys(),
      apiUnavailable: await fetch("/__mockapi/api/configuration").then(
        () => false,
        () => true
      ),
    }));
    expect(storage).toEqual({ caches: [], apiUnavailable: true });
    await expectNoUnreviewedAccessibilityViolations(page);
  } finally {
    await context.setOffline(false);
  }
  await page.getByRole("link", { name: "Reconnect to MockAPI" }).click();
  await expect(page.getByRole("heading", { name: /^Endpoints/ })).toBeVisible();
  await expect(page.locator("#connection-status")).toHaveText("Live");
});
