import {
  expect,
  test,
  importDocument,
  expectNoUnreviewedAccessibilityViolations,
  contrastRatio,
} from "./dashboard-fixtures.js";

async function loadExample(request) {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  await importDocument(request, example);
  return example;
}

test("API description supports hover, keyboard editing, whitespace, and native export @smoke", async ({
  page,
  request,
}, testInfo) => {
  await loadExample(request);
  const info = page.getByRole("button", { name: "Edit API description for /ex", exact: true });
  await expect(info).toBeVisible();
  await info.hover();
  const tooltip = page.getByRole("tooltip");
  await expect(tooltip).toContainText("This API demonstrates some of MockAPI's capabilities.");
  await expect(tooltip).toHaveCSS("text-transform", "none");
  await tooltip.hover();
  await expect(tooltip).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(tooltip).toBeHidden();
  await info.focus();
  await expect(tooltip).toBeVisible();
  if (testInfo.project.name !== "chromium-accessibility-preferences") {
    const colors = await info.evaluate((element) => {
      const style = getComputedStyle(element);
      return [style.color, style.backgroundColor];
    });
    expect(contrastRatio(...colors)).toBeGreaterThanOrEqual(4.5);
  }
  await page.keyboard.press("Enter");
  const dialog = page.locator("#api-description-dialog");
  await expect(dialog).toBeVisible();
  await expect(page.locator("#api-description-text")).toBeFocused();
  await expectNoUnreviewedAccessibilityViolations(page, "#api-description-dialog");
  const description = "  API overview\nSecond line <img src=x onerror=alert(1)>  ";
  await page.locator("#api-description-text").fill(description);
  await page.getByRole("button", { name: "Apply description", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(info).toBeFocused();
  await expect(tooltip).toContainText("<img src=x onerror=alert(1)>");
  await expect(tooltip.locator("img")).toHaveCount(0);
  const exported = await (await request.get("/__mockapi/api/configuration/export")).json();
  expect(exported.apiDescriptions["/ex"]).toBe(description);
  expect(exported.endpoints).toHaveLength(7);
  expect((await request.get("/ex/hello")).status()).toBe(200);
  await page.reload();
  await info.focus();
  await expect(tooltip).toContainText("Second line");
  await info.click();
  await page.locator("#api-description-text").fill("unsaved draft");
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(info).toBeFocused();
  await info.click();
  await expect(page.locator("#api-description-text")).toHaveValue(description);
});

test("API description stale drafts never overwrite live updates", async ({ page, request }) => {
  await loadExample(request);
  const info = page.getByRole("button", { name: "Edit API description for /ex", exact: true });
  await info.click();
  await page.locator("#api-description-text").fill("Stale draft");
  const configuration = await (await request.get("/__mockapi/api/configuration")).json();
  const winner = await request.put("/__mockapi/api/configuration/api-description", {
    headers: { "If-Match": configuration.etag },
    data: { path: "/ex", description: "Winning description" },
  });
  expect(winner.ok()).toBeTruthy();
  await expect(page.locator(".endpoint-group-row .api-description-tooltip p")).toHaveText("Winning description");
  await page.getByRole("button", { name: "Apply description", exact: true }).click();
  await expect(page.locator("#api-description-error")).toBeVisible();
  await expect(page.locator("#api-description-dialog")).toBeVisible();
  const current = await (await request.get("/__mockapi/api/configuration")).json();
  expect(current.apiDescriptions["/ex"]).toBe("Winning description");
  expect(current.revision).toBe((await winner.json()).revision);
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await info.click();
  await expect(page.locator("#api-description-text")).toHaveValue("Winning description");
});

test("empty descriptions stay cleared on example merges and retained metadata returns with its group", async ({
  page,
  request,
}) => {
  const example = await loadExample(request);
  const info = page.getByRole("button", { name: "Edit API description for /ex", exact: true });
  await info.click();
  await page.locator("#api-description-text").fill("");
  await page.getByRole("button", { name: "Apply description", exact: true }).click();
  await expect(page.locator("#api-description-dialog")).toBeHidden();
  const current = await (await request.get("/__mockapi/api/configuration")).json();
  const merge = await request.post("/__mockapi/api/configuration/example/merge?force=true", {
    headers: { "If-Match": current.etag },
  });
  expect((await merge.json()).applied).toBe(false);
  const exported = await (await request.get("/__mockapi/api/configuration/export")).json();
  expect(exported.apiDescriptions["/ex"]).toBe("");
  const deleted = await request.post("/__mockapi/api/endpoints/bulk", {
    headers: { "If-Match": current.etag },
    data: { endpointIds: example.endpoints.map((endpoint) => endpoint.id), operation: "delete" },
  });
  expect(deleted.ok()).toBeTruthy();
  await expect(info).toHaveCount(0);
  const remaining = await (await request.get("/__mockapi/api/configuration")).json();
  expect(remaining.apiDescriptions["/ex"]).toBe("");
  const restored = await request.post("/__mockapi/api/configuration/example/merge", {
    headers: { "If-Match": remaining.etag },
  });
  expect(restored.ok()).toBeTruthy();
  await info.focus();
  await expect(page.getByRole("tooltip")).toContainText("No API description yet.");
});

test("endpoint information is keyboard accessible and untrusted descriptions and responses remain inert text", async ({
  page,
  request,
}) => {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  const endpoint = example.endpoints.find((item) => item.path === "/ex/hello");
  const marker = "document.documentElement.dataset.descriptionExecuted='yes'";
  const payload = `Mixed Case\n<img src=x onerror="${marker}"><svg onload="${marker}"></svg><script>eval("${marker}")</script>`;
  endpoint.name = "Untrusted <img src=x onerror=alert(1)>";
  endpoint.description = payload;
  endpoint.response.contentType = "text/html";
  endpoint.response.body = payload;
  endpoint.response.headers = { "X-Untrusted-Text": ["<img src=x onerror=alert(1)>"] };
  await importDocument(request, { ...example, endpoints: [endpoint], apiDescriptions: { "/ex": payload } });
  await page.reload();

  const row = page.locator(".endpoint-row");
  const info = row.getByRole("button", { name: `Endpoint information for ${endpoint.name}`, exact: true });
  const tooltip = row.getByRole("tooltip", { includeHidden: true });
  await expect(info).toBeVisible();
  await expect(tooltip).toBeHidden();
  await info.hover();
  await expect(tooltip.locator("p")).toHaveText(payload);
  await expect(tooltip).toHaveCSS("text-transform", "none");
  await expect(tooltip.locator("p")).toHaveCSS("white-space", "pre-wrap");
  await tooltip.hover();
  await expect(tooltip).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(tooltip).toBeHidden();
  await info.focus();
  await expect(tooltip).toBeVisible();
  await expectNoUnreviewedAccessibilityViolations(page, ".workspace");
  await page.keyboard.press("Enter");
  await expect(page.locator("#field-description")).toHaveValue(payload);
  const edited = `${payload}\nEdited as plain text`;
  await page.locator("#field-description").fill(edited);
  await page.getByRole("button", { name: "Apply endpoint", exact: true }).click();
  await expect(info).toBeFocused();
  await expect(tooltip.locator("p")).toHaveText(edited);
  await page.keyboard.press("Escape");
  await info.click();
  await page.keyboard.press("Escape");
  await expect(info).toBeFocused();
  await page.keyboard.press("Escape");

  const groupInfo = page.getByRole("button", { name: "Edit API description for /ex", exact: true });
  await groupInfo.click();
  await expect(page.locator("#api-description-text")).toHaveValue(payload);
  await page.locator("#api-description-text").fill(edited);
  await page.getByRole("button", { name: "Apply description", exact: true }).click();
  await expect(page.locator(".endpoint-group-row .api-description-tooltip p")).toHaveText(edited);
  await page.keyboard.press("Escape");
  const exported = await (await request.get("/__mockapi/api/configuration/export")).json();
  expect(exported.apiDescriptions["/ex"]).toBe(edited);
  expect(exported.endpoints[0].description).toBe(edited);
  await page.reload();
  await info.focus();
  await expect(tooltip.locator("p")).toHaveText(edited);
  await page.keyboard.press("Escape");

  await row.getByRole("button", { name: "Test", exact: true }).click();
  await page.getByRole("button", { name: "Send request", exact: true }).click();
  await expect(page.locator("#test-response-body")).toHaveText(payload);
  await expect(page.locator("#test-response-headers")).toContainText("<img src=x onerror=alert(1)>");
  await expect(
    page
      .locator(".api-description-tooltip, .endpoint-name-heading, #test-response-body, #test-response-headers")
      .locator("img, svg, script")
  ).toHaveCount(0);
  await expect(page.locator("html")).not.toHaveAttribute("data-description-executed", "yes");
});

test("endpoints without descriptions still offer information and filter by hidden description text", async ({
  page,
  request,
}) => {
  const example = await (await request.get("/__mockapi/api/configuration/example")).json();
  const endpoint = example.endpoints[0];
  endpoint.description = "";
  await importDocument(request, { ...example, endpoints: [endpoint] });
  const info = page.getByRole("button", { name: `Endpoint information for ${endpoint.name}`, exact: true });
  await info.focus();
  await expect(page.getByRole("tooltip")).toContainText("No endpoint description yet.");
  await page.keyboard.press("Space");
  await page.locator("#field-description").fill("Searchable hidden information");
  await page.getByRole("button", { name: "Apply endpoint", exact: true }).click();
  await page.locator("#filter-text").fill("Searchable hidden information");
  await expect(page.locator(".endpoint-row")).toHaveCount(1);
  await expect(page.locator(".endpoint-row .api-description-tooltip")).toBeHidden();
});
