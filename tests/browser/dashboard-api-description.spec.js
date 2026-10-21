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

test("Trailhead endpoint previews fit their full text without scrolling on a desktop viewport", async ({
  page,
  request,
}) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  const example = await loadExample(request);
  for (const endpoint of example.endpoints) {
    const row = page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`);
    const info = row.getByRole("button", { name: `Endpoint information for ${endpoint.name}`, exact: true });
    await info.evaluate((button) => button.scrollIntoView({ block: "center" }));
    await info.focus();
    const tooltip = row.getByRole("tooltip", { includeHidden: true });
    await expect(tooltip).toBeVisible();
    const dimensions = await tooltip.evaluate((element) => {
      const bounds = element.getBoundingClientRect();
      return {
        scrollHeight: element.scrollHeight,
        clientHeight: element.clientHeight,
        height: bounds.height,
        top: bounds.top,
        bottom: bounds.bottom,
      };
    });
    expect(dimensions.scrollHeight, endpoint.name).toBeLessThanOrEqual(dimensions.clientHeight + 1);
    expect(dimensions.height).toBeLessThanOrEqual(400);
    expect(dimensions.top).toBeGreaterThanOrEqual(8);
    expect(dimensions.bottom).toBeLessThanOrEqual(892);
    await page.keyboard.press("Escape");
  }
});

test("Trailhead endpoint information shows bold description and technical implementation headings", async ({
  page,
  request,
}) => {
  const example = await loadExample(request);
  for (const path of ["/aoai/200/chat/completions", "/inventory/items", "/shipping/shipments"]) {
    const endpoint = example.endpoints.find((item) => item.path === path);
    expect(endpoint.description).toMatch(/^Description:\n.+\n\nTechnical implementation:\n.+$/);
    const row = page.locator(`.endpoint-row[data-endpoint-id="${endpoint.id}"]`);
    const info = row.getByRole("button", { name: `Endpoint information for ${endpoint.name}`, exact: true });
    const tooltip = row.getByRole("tooltip", { includeHidden: true });
    await info.scrollIntoViewIfNeeded();
    await info.focus();
    await expect(tooltip).toBeVisible();
    expect(await tooltip.locator("p").textContent()).toBe(endpoint.description);
    await expect(tooltip.locator("p")).toHaveCSS("white-space", "pre-wrap");
    await expect(tooltip.locator("p > strong")).toHaveText(["Description:", "Technical implementation:"]);
    for (const header of await tooltip.locator("p > strong").all()) {
      await expect(header).toHaveCSS("font-weight", "700");
    }
    await page.keyboard.press("Enter");
    await expect(page.locator("#endpoint-dialog")).toBeVisible();
    await expect(page.locator("#field-description")).toHaveValue(endpoint.description);
    await expectNoUnreviewedAccessibilityViolations(page, "#endpoint-dialog");
    await page.getByRole("button", { name: "Apply endpoint", exact: true }).click();
    await expect(page.locator("#endpoint-dialog")).toBeHidden();
    await expect(info).toBeFocused();
  }
  const exported = await (await request.get("/__mockapi/api/configuration/export")).json();
  expect(exported.endpoints.map((endpoint) => endpoint.description)).toEqual(
    example.endpoints.map((endpoint) => endpoint.description)
  );
});

test("API description supports hover, keyboard editing, whitespace, and native export @smoke", async ({
  page,
  request,
}, testInfo) => {
  await loadExample(request);
  const info = page.getByRole("button", { name: "Edit API description for /inventory", exact: true });
  await expect(info).toBeVisible();
  await info.hover();
  const tooltip = page.getByRole("tooltip");
  await expect(tooltip.locator("strong")).toHaveText("inventory");
  await expect(tooltip).toContainText("Trailhead Supply");
  await expect(tooltip).toContainText("stock");
  await expect(tooltip).toContainText("These static examples");
  await expect(tooltip.locator("p")).toHaveCSS("margin-top", "8px");
  await expect(tooltip.locator("small").last()).toHaveText("Click the information button to edit.");
  await expect(tooltip).toHaveCSS("text-transform", "none");
  const previewBounds = await tooltip.boundingBox();
  await page.mouse.move(previewBounds.x + previewBounds.width / 2, previewBounds.y + previewBounds.height / 2);
  await expect(tooltip).toBeHidden();
  await info.hover();
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
  expect(exported.apiDescriptions["/inventory"]).toBe(description);
  expect(exported.endpoints).toHaveLength(41);
  expect((await request.get("/inventory/items")).status()).toBe(200);
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
  const info = page.getByRole("button", { name: "Edit API description for /inventory", exact: true });
  await info.click();
  await page.locator("#api-description-text").fill("Stale draft");
  const configuration = await (await request.get("/__mockapi/api/configuration")).json();
  const winner = await request.put("/__mockapi/api/configuration/api-description", {
    headers: { "If-Match": configuration.etag },
    data: { path: "/inventory", description: "Winning description" },
  });
  expect(winner.ok()).toBeTruthy();
  const group = page.locator(".endpoint-group-row").filter({ has: info });
  await expect(group.locator(".api-description-tooltip p")).toHaveText("Winning description");
  await page.getByRole("button", { name: "Apply description", exact: true }).click();
  await expect(page.locator("#api-description-error")).toBeVisible();
  await expect(page.locator("#api-description-dialog")).toBeVisible();
  const current = await (await request.get("/__mockapi/api/configuration")).json();
  expect(current.apiDescriptions["/inventory"]).toBe("Winning description");
  expect(current.revision).toBe((await winner.json()).revision);
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await info.click();
  await expect(page.locator("#api-description-text")).toHaveValue("Winning description");
});

test("information previews stay closed when API and endpoint editors restore focus", async ({ page, request }) => {
  const example = await loadExample(request);
  const endpoint = example.endpoints.find((item) => item.path === "/inventory/items");

  const groupInfo = page.getByRole("button", { name: "Edit API description for /inventory", exact: true });
  const groupTooltip = page
    .locator(".endpoint-group-row")
    .filter({ has: groupInfo })
    .locator(".api-description-tooltip");
  await groupInfo.focus();
  await expect(groupTooltip).toBeVisible();
  await page.keyboard.press("Enter");
  await expect(page.locator("#api-description-dialog")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(groupInfo).toBeFocused();
  await expect(groupTooltip).toBeHidden();

  const endpointRow = page.locator(`[data-endpoint-id="${endpoint.id}"]`);
  const endpointInfo = endpointRow.getByRole("button", {
    name: `Endpoint information for ${endpoint.name}`,
    exact: true,
  });
  const endpointTooltip = endpointRow.getByRole("tooltip", { includeHidden: true });
  await endpointInfo.focus();
  await expect(endpointTooltip).toBeVisible();
  await page.keyboard.press("Enter");
  await expect(page.locator("#endpoint-dialog")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(endpointInfo).toBeFocused();
  await expect(endpointTooltip).toBeHidden();

  await groupInfo.focus();
  await expect(groupTooltip).toBeVisible();
});

test("empty descriptions stay cleared on example merges and retained metadata returns with its group", async ({
  page,
  request,
}) => {
  const example = await loadExample(request);
  const info = page.getByRole("button", { name: "Edit API description for /inventory", exact: true });
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
  expect(exported.apiDescriptions["/inventory"]).toBe("");
  const deleted = await request.post("/__mockapi/api/endpoints/bulk", {
    headers: { "If-Match": current.etag },
    data: { endpointIds: example.endpoints.map((endpoint) => endpoint.id), operation: "delete" },
  });
  expect(deleted.ok()).toBeTruthy();
  await expect(info).toHaveCount(0);
  const remaining = await (await request.get("/__mockapi/api/configuration")).json();
  expect(remaining.apiDescriptions["/inventory"]).toBe("");
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
  const endpoint = example.endpoints.find((item) => item.path === "/inventory/items");
  const marker = "document.documentElement.dataset.descriptionExecuted='yes'";
  const payload = `Description:\nMixed Case <img src=x onerror="${marker}">\n\nTechnical implementation:\n<svg onload="${marker}"></svg><script>eval("${marker}")</script>`;
  endpoint.name = "Untrusted <img src=x onerror=alert(1)>";
  endpoint.description = payload;
  endpoint.response.contentType = "text/html";
  endpoint.response.body = payload;
  endpoint.response.headers = { "X-Untrusted-Text": ["<img src=x onerror=alert(1)>"] };
  await importDocument(request, { ...example, endpoints: [endpoint], apiDescriptions: { "/inventory": payload } });
  await page.reload();

  const row = page.locator(".endpoint-row");
  const info = row.getByRole("button", { name: `Endpoint information for ${endpoint.name}`, exact: true });
  const tooltip = row.getByRole("tooltip", { includeHidden: true });
  await expect(info).toBeVisible();
  await expect(tooltip).toBeHidden();
  await info.hover();
  await expect(tooltip.locator("p")).toHaveText(payload);
  await expect(tooltip.locator(":scope > strong")).toHaveText(endpoint.name);
  await expect(tooltip.locator("p > strong")).toHaveText(["Description:", "Technical implementation:"]);
  await expect(tooltip.locator("p")).toHaveCSS("margin-top", "8px");
  await expect(tooltip.locator("small").last()).toHaveText("Click the information button to edit.");
  await expect(tooltip).toHaveCSS("text-transform", "none");
  await expect(tooltip.locator("p")).toHaveCSS("white-space", "pre-wrap");
  const previewBounds = await tooltip.boundingBox();
  await page.mouse.move(previewBounds.x + previewBounds.width / 2, previewBounds.y + previewBounds.height / 2);
  await expect(tooltip).toBeHidden();
  await info.hover();
  await expect(tooltip).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(tooltip).toBeHidden();
  await info.focus();
  await expect(tooltip).toBeVisible();
  await page.mouse.move(0, 0);
  await info.hover();
  await page.mouse.move(previewBounds.x + previewBounds.width / 2, previewBounds.y + previewBounds.height / 2);
  await expect(info).toBeFocused();
  await expect(tooltip).toBeHidden();
  await info.blur();
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

  const groupInfo = page.getByRole("button", { name: "Edit API description for /inventory", exact: true });
  await groupInfo.click();
  await expect(page.locator("#api-description-text")).toHaveValue(payload);
  await page.locator("#api-description-text").fill(edited);
  await page.getByRole("button", { name: "Apply description", exact: true }).click();
  await expect(page.locator(".endpoint-group-row .api-description-tooltip p")).toHaveText(edited);
  await page.keyboard.press("Escape");
  const exported = await (await request.get("/__mockapi/api/configuration/export")).json();
  expect(exported.apiDescriptions["/inventory"]).toBe(edited);
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
  const dialog = page.locator("#endpoint-dialog");
  await expect(dialog).toBeVisible();
  await page.locator("#field-description").fill("Searchable hidden information");
  await page.getByRole("button", { name: "Apply endpoint", exact: true }).click();
  await expect(dialog).toBeHidden();
  // Wait for the dialog's asynchronous close handler to restore focus before moving to the filter.
  await expect(info).toBeFocused();
  const filter = page.locator("#filter-text");
  await filter.fill("Searchable hidden information");
  await expect(filter).toBeFocused();
  await expect(page.locator(".endpoint-row")).toHaveCount(1);
  await expect(page.locator(".endpoint-row .api-description-tooltip")).toBeHidden();
  await expect(page.locator(".endpoint-row .api-description-tooltip p > strong")).toHaveCount(0);
  await filter.fill("Description that does not match");
  await expect(page.locator(".endpoint-row")).toHaveCount(0);
});
