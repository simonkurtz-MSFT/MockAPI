const { test, expect } = require("@playwright/test");
const AxeBuilder = require("@axe-core/playwright").default;

test.beforeEach(async ({ page }) => {
  // Exercise tag initialization without sending preview or test traffic to Google.
  await page.route("https://www.googletagmanager.com/gtag/js?*", (route) =>
    route.fulfill({ contentType: "application/javascript", body: "" })
  );
});

test("initializes the supplied Google Analytics tag once", async ({ page }) => {
  const tagUrl = "https://www.googletagmanager.com/gtag/js?id=G-XQZ0DQP020";
  const tagRequest = page.waitForRequest(tagUrl);
  await page.goto("./");
  await tagRequest;
  const tag = page.locator(`script[src="${tagUrl}"]`);
  await expect(tag).toHaveCount(1);
  await expect(tag).toHaveAttribute("async", "");
  const commands = await page.evaluate(() => window.dataLayer.map((command) => Array.from(command)));
  expect(commands).toEqual([
    ["js", expect.any(Date)],
    ["config", "G-XQZ0DQP020"],
  ]);
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
        await page.locator(".preview img").getAttribute("alt")
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
