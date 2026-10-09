const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;
const fs = require("node:fs");
const path = require("node:path");
const { renderSiteStyles } = require("../../scripts/build-site.cjs");

const dashboardRoot = path.join(__dirname, "..", "..", "src", "MockAPI", "wwwroot");
const dashboardStyles = fs.readFileSync(path.join(dashboardRoot, "app.css"), "utf8");

test.beforeEach(async ({ page }) => {
  // Exercise tag initialization without sending test traffic to Google.
  await page.route("https://www.googletagmanager.com/**", (route) =>
    route.fulfill({ contentType: "application/javascript", body: "" })
  );
  await page.route(/https:\/\/(?:[^/]+\.)?(?:google-analytics|analytics\.google)\.com\//, (route) => route.abort());
});

async function serveProductionSite(page, baseURL) {
  await page.route("https://mockapi.simondoescloud.com/**", async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: new URL(`.${url.pathname}${url.search}`, baseURL).href });
    await route.fulfill({ response });
  });
}

test("initializes direct GA4 once on production with one privacy-limited automatic page view", async ({
  page,
  baseURL,
}) => {
  await serveProductionSite(page, baseURL);
  const tagUrl = "https://www.googletagmanager.com/gtag/js?id=G-XQZ0DQP020";
  const tagRequest = page.waitForRequest(tagUrl);
  await page.goto("https://mockapi.simondoescloud.com/?private=value#private-fragment");
  await tagRequest;
  const tag = page.locator(`script[src="${tagUrl}"]`);
  await expect(tag).toHaveCount(1);
  await expect(tag).toHaveAttribute("async", "");
  const dataLayer = await page.evaluate(() => window.dataLayer.map((command) => Array.from(command)));
  expect(dataLayer).toEqual([
    [
      "consent",
      "default",
      {
        ad_storage: "denied",
        ad_user_data: "denied",
        ad_personalization: "denied",
        analytics_storage: "granted",
      },
    ],
    ["set", "ads_data_redaction", true],
    ["js", expect.any(Date)],
    [
      "config",
      "G-XQZ0DQP020",
      {
        allow_google_signals: false,
        allow_ad_personalization_signals: false,
        page_location: "https://mockapi.simondoescloud.com/",
        page_referrer: "",
        page_title: "MockAPI | Open-Source, Self-Hosted Mock API Server",
        cookie_flags: "SameSite=Lax;Secure",
      },
    ],
  ]);
  await expect(page.locator('script[src*="/gtm.js"], iframe[src*="googletagmanager"]')).toHaveCount(0);
});

test("does not load analytics or create its queue during local previews", async ({ page }) => {
  const requests = [];
  page.on("request", (request) => {
    if (request.url().includes("googletagmanager.com")) requests.push(request.url());
  });
  await page.goto("./");
  expect(await page.evaluate(() => window.dataLayer)).toBeUndefined();
  expect(requests).toEqual([]);
});

for (const privacySignal of ["globalPrivacyControl", "doNotTrack"]) {
  test(`loads analytics on production with ${privacySignal}`, async ({ page, baseURL }) => {
    await serveProductionSite(page, baseURL);
    await page.addInitScript((setting) => {
      Object.defineProperty(navigator, setting, { value: setting === "doNotTrack" ? "1" : true });
    }, privacySignal);
    const tagRequest = page.waitForRequest("https://www.googletagmanager.com/gtag/js?id=G-XQZ0DQP020");
    await page.goto("https://mockapi.simondoescloud.com/");
    await tagRequest;
    expect(await page.evaluate(() => window.dataLayer.length)).toBeGreaterThan(0);
  });
}

test("does not load analytics on production with the explicit Google Analytics opt-out", async ({ page, baseURL }) => {
  await serveProductionSite(page, baseURL);
  await page.addInitScript(() => {
    window["ga-disable-G-XQZ0DQP020"] = true;
  });
  const requests = [];
  page.on("request", (request) => {
    if (request.url().includes("googletagmanager.com")) requests.push(request.url());
  });
  await page.goto("https://mockapi.simondoescloud.com/");
  expect(await page.evaluate(() => window.dataLayer)).toBeUndefined();
  expect(requests).toEqual([]);
});

test("reports Google tag load failures without breaking the documentation", async ({ page, baseURL }) => {
  await serveProductionSite(page, baseURL);
  await page.route("https://www.googletagmanager.com/**", (route) => route.abort());
  const warning = page.waitForEvent("console", {
    predicate: (message) => message.text() === "Documentation site Google Analytics could not be loaded.",
  });
  await page.goto("https://mockapi.simondoescloud.com/");
  expect((await warning).type()).toBe("warning");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Configure the response.");
});

test("quick starts, assets and accessibility work under the Pages repository prefix", async ({ page }) => {
  const failures = [];
  page.on("pageerror", (error) => failures.push(error.message));
  page.on("requestfailed", (request) => failures.push(request.url()));
  page.on("response", (response) => {
    if (response.status() >= 400) failures.push(response.url());
  });
  await page.goto("./");
  await expect(page).toHaveTitle("MockAPI | Open-Source, Self-Hosted Mock API Server");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Configure the response.");
  await page.getByRole("link", { name: "Get started", exact: true }).last().click();
  await expect(page).toHaveURL(/#start$/);
  await expect(page.locator("body")).not.toContainText("Codespaces");
  await expect(page.locator('a[href*="codespaces"]')).toHaveCount(0);
  expect(
    await page.locator("img").evaluateAll((images) => images.every((image) => image.complete && image.naturalWidth > 0))
  ).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations).toEqual([]);
  expect(failures).toEqual([]);
});

test("footer links to Simon Kurtz's website in a protected new tab", async ({ page }) => {
  await page.goto("./");
  const authorLink = page.getByRole("contentinfo").getByRole("link", { name: "Simon Kurtz", exact: true });
  await expect(authorLink).toBeVisible();
  await expect(authorLink).toHaveAttribute("href", "https://www.simondoescloud.com");
  await expect(authorLink).toHaveAttribute("target", "_blank");
  await expect(authorLink).toHaveAttribute("rel", "noopener noreferrer");
});

test("dashboard-style theme toggle uses the initial system theme and persists explicit choices", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await page.goto("./");
  const theme = page.getByRole("button", { name: "Toggle color theme" });
  const root = page.locator("html");
  await expect(theme).toBeVisible();
  await expect(page.getByRole("combobox")).toHaveCount(0);
  await expect(root).toHaveAttribute("data-theme", "dark");
  await theme.click();
  await expect(root).toHaveAttribute("data-theme", "light");
  await expect(root).toHaveCSS("color-scheme", "light");
  await page.reload();
  await expect(root).toHaveAttribute("data-theme", "light");
  await theme.focus();
  await page.keyboard.press("Space");
  await page.emulateMedia({ colorScheme: "light" });
  await expect(root).toHaveAttribute("data-theme", "dark");
  await expect(root).toHaveCSS("color-scheme", "dark");
  await page.reload();
  await expect(root).toHaveAttribute("data-theme", "dark");
  await theme.focus();
  await page.keyboard.press("Enter");
  await expect(root).toHaveAttribute("data-theme", "light");
  await page.emulateMedia({ colorScheme: "dark" });
  await expect(root).toHaveAttribute("data-theme", "light");
  await page.reload();
  await expect(root).toHaveAttribute("data-theme", "light");
  for (const mode of ["light", "dark"]) {
    if ((await root.getAttribute("data-theme")) !== mode) await theme.click();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations).toEqual([]);
  }
});

test("theme control uses the dashboard icon and works without browser storage", async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, "localStorage", {
      get() {
        throw new Error("Storage disabled");
      },
    });
  });
  await page.emulateMedia({ colorScheme: "light" });
  await page.goto("./");
  await page.getByRole("button", { name: "Toggle color theme" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(page.locator("#theme-status")).toHaveText(
    "Theme changed for this visit. Browser storage is unavailable."
  );
  const dashboard = fs.readFileSync(path.join(dashboardRoot, "dashboard-dom.js"), "utf8");
  const themePaths = dashboard.match(/theme:\s*'([^']+)'/)[1];
  const paths = await page.locator("#theme-toggle svg path").getAttribute("d");
  expect(themePaths).toContain(`<path d="${paths}"/>`);
  await expect(page.locator("#theme-toggle svg circle")).toHaveAttribute("r", "4");
});

test("first paint applies only valid site preferences, independently of the dashboard", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await page.addInitScript(() => {
    localStorage.setItem("mockapi.theme", "light");
    localStorage.setItem("mockapi.preferences", JSON.stringify({ version: 1, theme: "light" }));
  });
  await page.goto("./");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  for (const [saved, resolved] of [
    ["light", "light"],
    ["dark", "dark"],
    ["system", "dark"],
    ["invalid", "dark"],
  ]) {
    await page.evaluate((value) => localStorage.setItem("mockapi.site.theme", value), saved);
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", resolved);
  }
});

test("light and dark theme tokens and button states exactly match the dashboard", async ({ page, context }) => {
  await page.emulateMedia({ colorScheme: "light" });
  await page.goto("./");
  const toggle = page.getByRole("button", { name: "Toggle color theme" });
  const reference = await context.newPage();
  try {
    await reference.setContent(
      `<header class="app-header"><button class="icon-button icon-theme" aria-label="Toggle color theme">${await toggle.innerHTML()}</button></header>`
    );
    await reference.addStyleTag({ content: dashboardStyles });
    const referenceToggle = reference.getByRole("button", { name: "Toggle color theme" });
    const properties = [
      "background-color",
      "border-top-color",
      "border-radius",
      "box-shadow",
      "color",
      "width",
      "height",
      "padding",
      "outline",
      "outline-offset",
      "transform",
    ];
    const siteStyles = renderSiteStyles();
    const tokens = [...new Set([...siteStyles.matchAll(/(--cp-[a-z-]+):/g)].map((match) => match[1]))];
    for (const mode of ["light", "dark"]) {
      if ((await page.locator("html").getAttribute("data-theme")) !== mode) await toggle.click();
      await reference.locator("html").evaluate((root, theme) => (root.dataset.theme = theme), mode);
      await page.mouse.move(0, 0);
      await reference.mouse.move(0, 0);
      await toggle.blur();
      await referenceToggle.blur();
      const referenceTokens = await reference
        .locator("html")
        .evaluate((root, names) => names.map((name) => getComputedStyle(root).getPropertyValue(name).trim()), tokens);
      const siteTokens = await page
        .locator("html")
        .evaluate((root, names) => names.map((name) => getComputedStyle(root).getPropertyValue(name).trim()), tokens);
      expect(siteTokens).toEqual(referenceTokens);
      for (const state of ["default", "hover", "focus"]) {
        if (state === "hover") {
          await referenceToggle.hover();
          await toggle.hover();
        }
        if (state === "focus") {
          await page.mouse.move(0, 0);
          await reference.mouse.move(0, 0);
          await referenceToggle.focus();
          await toggle.focus();
        }
        await expect(referenceToggle).toHaveCSS("transform", state === "hover" ? "matrix(1, 0, 0, 1, 0, -1)" : "none");
        // A settled transform does not guarantee that color and shadow transitions have finished.
        for (const button of [referenceToggle, toggle]) {
          await expect.poll(() => button.evaluate((element) => element.getAnimations().length)).toBe(0);
        }
        for (const property of properties) {
          const expected = await referenceToggle.evaluate(
            (button, name) => getComputedStyle(button).getPropertyValue(name),
            property
          );
          await expect(toggle).toHaveCSS(property, expected);
        }
      }
      await expect(toggle.locator("svg")).toHaveCSS("width", "18px");
      await expect(toggle.locator("svg")).toHaveCSS("height", "18px");
      const primary = page.getByRole("link", { name: "Get started", exact: true }).last();
      await expect(primary).toHaveCSS("background-color", mode === "light" ? "rgb(7, 94, 168)" : "rgb(105, 184, 255)");
      await expect(primary).toHaveCSS("color", mode === "light" ? "rgb(255, 255, 255)" : "rgb(8, 33, 59)");
    }
  } finally {
    await reference.close();
  }
});

test("bundles the dashboard theme tokens as the documentation theme source", () => {
  const siteSource = fs.readFileSync(path.join(__dirname, "..", "..", "site", "site.css"), "utf8");
  const sharedTheme = dashboardStyles.slice(
    dashboardStyles.indexOf("/* Theme tokens */"),
    dashboardStyles.indexOf("/* Foundations and keyboard navigation */")
  );
  const bundledStyles = renderSiteStyles();

  expect(siteSource).not.toMatch(/--cp-(?:bg|surface|text|accent|border|link):\s*#/);
  expect(bundledStyles).toContain(sharedTheme.trim());
  expect(bundledStyles).toContain("@media (prefers-color-scheme: dark) {\n  :root:not([data-theme])");
  expect(bundledStyles).toContain("font: 100%/1.65 var(--cp-font-sans);");
});

test("preview rejects assets outside the publication allowlist", async ({ request }) => {
  for (const name of [".env", "start.ps1", "package.json", "site.js", "unknown.js"]) {
    expect((await request.get(name)).status()).toBe(404);
  }
});

test.describe("search engine discovery", () => {
  test.use({ javaScriptEnabled: false });

  test("serves descriptive content, canonical metadata and structured data without JavaScript", async ({ page }) => {
    const canonicalUrl = "https://mockapi.simondoescloud.com/";
    await page.goto("./");
    const title = await page.title();
    const description = await page.locator('meta[name="description"]').getAttribute("content");
    expect(title.length).toBeLessThanOrEqual(60);
    expect(description.length).toBeLessThanOrEqual(160);
    expect(description).toContain("self-hosted mock API server");
    await expect(page.locator('meta[name="author"]')).toHaveAttribute("content", "Simon Kurtz");
    await expect(page.locator('link[rel="canonical"]')).toHaveAttribute("href", canonicalUrl);
    await expect(page.getByRole("heading", { level: 1 })).toHaveCount(1);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("MockAPI");
    await expect(page.locator(".lead")).toContainText("self-hosted mock API server");
    await expect(page.locator('meta[property="og:type"]')).toHaveAttribute("content", "website");
    await expect(page.locator('meta[property="og:site_name"]')).toHaveAttribute("content", "MockAPI");
    await expect(page.locator('meta[property="og:url"]')).toHaveAttribute("content", canonicalUrl);
    await expect(page.locator('meta[name="twitter:card"]')).toHaveAttribute("content", "summary_large_image");
    for (const [attribute, prefix] of [
      ["property", "og"],
      ["name", "twitter"],
    ]) {
      await expect(page.locator(`meta[${attribute}="${prefix}:title"]`)).toHaveAttribute("content", title);
      await expect(page.locator(`meta[${attribute}="${prefix}:description"]`)).toHaveAttribute("content", description);
      await expect(page.locator(`meta[${attribute}="${prefix}:image"]`)).toHaveAttribute(
        "content",
        `${canonicalUrl}dashboard.png`
      );
      await expect(page.locator(`meta[${attribute}="${prefix}:image:alt"]`)).toHaveAttribute(
        "content",
        await page.locator(".preview img").first().getAttribute("alt")
      );
    }
    const data = JSON.parse(await page.locator('script[type="application/ld+json"]').textContent());
    expect(data["@context"]).toBe("https://schema.org");
    expect(data["@graph"]).toHaveLength(2);
    for (const entity of data["@graph"]) {
      expect(entity.author).toEqual({ "@type": "Person", name: "Simon Kurtz" });
    }
    expect(data["@graph"][0]).toMatchObject({ "@type": "WebSite", name: "MockAPI", url: canonicalUrl });
    expect(data["@graph"][1]).toMatchObject({
      "@type": ["SoftwareApplication", "SoftwareSourceCode"],
      name: "MockAPI",
      url: canonicalUrl,
      description,
      applicationCategory: "DeveloperApplication",
      codeRepository: "https://github.com/simonkurtz-MSFT/MockAPI",
      screenshot: `${canonicalUrl}dashboard.png`,
    });
  });

  test("publishes crawler files with the canonical URL and correct content types", async ({ page, request }) => {
    await page.goto("./");
    const canonicalUrl = await page.locator('link[rel="canonical"]').getAttribute("href");
    const robots = await request.get("robots.txt");
    expect(robots.status()).toBe(200);
    expect(robots.headers()["content-type"]).toBe("text/plain");
    expect((await robots.text()).replaceAll("\r\n", "\n")).toBe(
      `User-agent: *\nAllow: /\n\nSitemap: ${canonicalUrl}sitemap.xml\n`
    );
    const sitemap = await request.get("sitemap.xml");
    expect(sitemap.status()).toBe(200);
    expect(sitemap.headers()["content-type"]).toBe("application/xml");
    const entries = await page.evaluate(
      (xml) => {
        const document = new DOMParser().parseFromString(xml, "application/xml");
        return {
          errors: document.querySelectorAll("parsererror").length,
          namespace: document.documentElement.namespaceURI,
          locations: [...document.querySelectorAll("url > loc")].map((location) => location.textContent),
        };
      },
      await sitemap.text()
    );
    expect(entries).toEqual({
      errors: 0,
      namespace: "http://www.sitemaps.org/schemas/sitemap/0.9",
      locations: [canonicalUrl],
    });
    const image = await request.get("dashboard.png");
    expect(image.status()).toBe(200);
    expect(image.headers()["content-type"]).toBe("image/png");
  });
});

test("dashboard gallery supports named views, wrapping controls, keyboard navigation, and full-size images", async ({
  page,
}) => {
  await page.goto("./");
  const gallery = page.getByRole("region", { name: "Dashboard screenshots" });
  const slides = gallery.locator(".gallery-slide");
  await expect(slides).toHaveCount(3);
  await expect(slides.nth(0)).toBeVisible();
  await expect(slides.nth(1)).toBeHidden();
  await expect(slides.nth(2)).toBeHidden();
  await expect(gallery.getByRole("button", { name: "Overview", exact: true })).toHaveAttribute("aria-pressed", "true");
  await gallery.getByRole("button", { name: "Previous screenshot" }).click();
  await expect(slides.nth(2)).toBeVisible();
  await expect(gallery.getByRole("status")).toHaveText("Screenshot 3 of 3: Request log");
  await gallery.getByRole("button", { name: "Next screenshot" }).click();
  await expect(slides.nth(0)).toBeVisible();
  const endpoints = gallery.getByRole("button", { name: "Endpoints", exact: true });
  await endpoints.click();
  await expect(slides.nth(1)).toBeVisible();
  await expect(endpoints).toHaveAttribute("aria-pressed", "true");
  await expect(gallery.getByRole("button", { name: "Overview", exact: true })).toHaveAttribute("aria-pressed", "false");
  await endpoints.focus();
  await page.keyboard.press("ArrowRight");
  await expect(slides.nth(2)).toBeVisible();
  await expect(endpoints).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(slides.nth(0)).toBeVisible();
  await page.keyboard.press("ArrowLeft");
  await expect(slides.nth(2)).toBeVisible();
  await page.keyboard.press("Shift+ArrowLeft");
  await expect(slides.nth(2)).toBeVisible();
  await page.getByRole("button", { name: "Toggle color theme" }).click();
  await expect(slides.nth(2)).toBeVisible();
  const image = gallery.getByRole("link", { name: "Open request log at full size (opens in a new tab)" });
  await expect(image).toHaveAttribute("href", "./dashboard-request-log.png");
  await expect(image).toHaveAttribute("target", "_blank");
  await expect(image).toHaveAttribute("rel", "noopener");
  expect(
    await gallery
      .locator("img")
      .evaluateAll((images) => images.map((image) => [image.naturalWidth, image.naturalHeight]))
  ).toEqual([
    [1920, 1500],
    [922, 1000],
    [922, 574],
  ]);
  expect(
    await gallery
      .locator("img")
      .evaluateAll((images) =>
        images.every(
          (image) =>
            Number(image.getAttribute("width")) === image.naturalWidth &&
            Number(image.getAttribute("height")) === image.naturalHeight
        )
      )
  ).toBe(true);
});

test("every gallery view is accessible in light and dark mode and fits a narrow viewport", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto("./");
  const gallery = page.getByRole("region", { name: "Dashboard screenshots" });
  for (const mode of ["light", "dark"]) {
    if ((await page.locator("html").getAttribute("data-theme")) !== mode) {
      await page.getByRole("button", { name: "Toggle color theme" }).click();
    }
    for (const name of ["Overview", "Endpoints", "Request log"]) {
      await gallery.getByRole("button", { name, exact: true }).click();
      await expect(gallery.locator(".gallery-slide:visible")).toHaveCount(1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      const results = await new AxeBuilder({ page })
        .include("#dashboard-gallery")
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze();
      expect(results.violations).toEqual([]);
    }
  }
  await page.emulateMedia({ forcedColors: "active" });
  await expect(gallery.getByRole("button", { name: "Next screenshot" })).toBeVisible();
  await gallery.getByRole("button", { name: "Next screenshot" }).click();
  await expect(gallery.getByRole("status")).toHaveText("Screenshot 1 of 3: Dashboard overview");
});

test.describe("gallery without JavaScript", () => {
  test.use({ javaScriptEnabled: false });
  test("keeps all screenshots and full-size links available", async ({ page }) => {
    await page.goto("./");
    const gallery = page.getByRole("region", { name: "Dashboard screenshots" });
    await expect(gallery.locator(".gallery-slide:visible")).toHaveCount(3);
    await expect(gallery.getByRole("link")).toHaveCount(3);
    await expect(gallery.locator("#gallery-controls")).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
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
