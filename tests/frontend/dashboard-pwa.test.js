import { readFileSync } from "node:fs";
import vm from "node:vm";
import { describe, expect, it, vi } from "vitest";

const workerSource = readFileSync("src/MockAPI/wwwroot/service-worker.js", "utf8");

function createWorker(fetch = vi.fn()) {
  const handlers = new Map();
  const claim = vi.fn().mockResolvedValue(undefined);
  vm.runInNewContext(workerSource, {
    self: {
      location: { origin: "https://mockapi.example" },
      clients: { claim },
      addEventListener: (name, handler) => handlers.set(name, handler),
    },
    URL,
    Response,
    fetch,
  });
  return { handlers, claim, fetch };
}

function navigation(overrides = {}) {
  return {
    request: {
      url: "https://mockapi.example/",
      method: "GET",
      mode: "navigate",
      ...overrides,
    },
    respondWith: vi.fn(),
  };
}

describe("dashboard network-only service worker", () => {
  it("claims existing dashboard clients on activation without caching content", async () => {
    const worker = createWorker();
    const waitUntil = vi.fn();
    worker.handlers.get("activate")({ waitUntil });
    await waitUntil.mock.calls[0][0];
    expect(worker.claim).toHaveBeenCalledOnce();
    expect([...worker.handlers.keys()]).toEqual(["activate", "fetch"]);
  });

  it.each([
    { url: "https://mockapi.example/__mockapi/api/configuration" },
    { url: "https://mockapi.example/__mockapi/api/dashboard/events" },
    { url: "https://mockapi.example/health/ready" },
    { url: "https://mockapi.example/inventory" },
    { url: "https://mockapi.example/app.js" },
    { url: "https://other.example/" },
    { method: "POST" },
    { method: "HEAD" },
    { mode: "cors" },
  ])("does not intercept non-dashboard navigation: %j", (overrides) => {
    const worker = createWorker();
    const event = navigation(overrides);
    worker.handlers.get("fetch")(event);
    expect(event.respondWith).not.toHaveBeenCalled();
    expect(worker.fetch).not.toHaveBeenCalled();
  });

  it.each([200, 401, 403, 500, 503])("preserves live HTTP %i responses without a fallback", async (status) => {
    const response = new Response("live response", { status });
    const worker = createWorker(vi.fn().mockResolvedValue(response));
    const event = navigation({ url: "https://mockapi.example/?tutorial=skip" });
    worker.handlers.get("fetch")(event);
    expect(await event.respondWith.mock.calls[0][0]).toBe(response);
    expect(worker.fetch).toHaveBeenCalledWith(event.request, { cache: "no-store" });
  });

  it("returns only a non-storable reconnect page when navigation fails", async () => {
    const worker = createWorker(vi.fn().mockRejectedValue(new TypeError("Offline")));
    const event = navigation();
    worker.handlers.get("fetch")(event);
    const response = await event.respondWith.mock.calls[0][0];
    expect(response.status).toBe(503);
    expect(response.headers.get("Cache-Control")).toBe("no-store");
    expect(response.headers.get("Content-Type")).toBe("text/html; charset=utf-8");
    expect(response.headers.get("Content-Security-Policy")).toContain("default-src 'none'");
    const html = await response.text();
    expect(html).toContain('href="/"');
    expect(html).toContain("Changes are not queued or replayed");
    expect(html).not.toContain("<script");
  });
});
