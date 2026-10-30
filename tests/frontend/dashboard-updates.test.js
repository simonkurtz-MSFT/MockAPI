import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  createDashboardUpdateMonitor,
  DASHBOARD_UPDATE_DISMISSED_KEY,
  isNewerDashboardRelease,
} from "../../src/MockAPI/wwwroot/dashboard-updates.js";

const hour = 60 * 60 * 1000;
const flush = () => vi.advanceTimersByTimeAsync(0);

function harness(options = {}) {
  const entries = new Map();
  const sessionStorage = {
    getItem: vi.fn((key) => entries.get(key) ?? null),
    setItem: vi.fn((key, value) => entries.set(key, value)),
  };
  const request = vi.fn(async () => new Response("1.2.3"));
  const onAvailable = vi.fn();
  const onError = vi.fn();
  const monitor = createDashboardUpdateMonitor({
    currentVersion: "1.2.3",
    request,
    sessionStorage,
    onAvailable,
    onError,
    ...options,
  });
  return { monitor, entries, sessionStorage, request, onAvailable, onError };
}

describe("dashboard release comparison", () => {
  it.each([
    ["1.2.4", "1.2.3"],
    ["1.10.0", "1.9.9"],
    ["2.0.0", "1.99.99"],
    ["100.0.0", "99.99.99"],
    ["1.0.0-alpha.1", "1.0.0-alpha"],
    ["1.0.0-alpha.beta", "1.0.0-alpha.1"],
    ["1.0.0-beta", "1.0.0-alpha.beta"],
    ["1.0.0-beta.2", "1.0.0-beta.1"],
    ["1.0.0-beta.11", "1.0.0-beta.2"],
    ["1.0.0-rc.1", "1.0.0-beta.11"],
    ["1.0.0", "1.0.0-rc.1"],
    ["1.0.0-beta", "1.0.0-0"],
    ["9007199254740993.0.0", "9007199254740992.0.0"],
    ["1.0.0-9007199254740993", "1.0.0-9007199254740992"],
  ])("recognizes %s as newer than %s without numeric rounding", (available, current) => {
    expect(isNewerDashboardRelease(available, current)).toBe(true);
    expect(isNewerDashboardRelease(current, available)).toBe(false);
    expect(isNewerDashboardRelease(available, available)).toBe(false);
  });

  it.each([
    ["1.2.3+new-commit", "1.2.3+old-commit"],
    ["1.2.3", "1.2.3+old-commit"],
    ["1.2.3-beta.1+build.2", "1.2.3-beta.1+build.1"],
  ])("ignores same-release commit/build metadata: %s and %s", (available, current) => {
    expect(isNewerDashboardRelease(available, current)).toBe(false);
    expect(isNewerDashboardRelease(current, available)).toBe(false);
  });

  it.each([
    null,
    123,
    "",
    "v1.2.3",
    "1.2",
    "01.2.3",
    "1.02.3",
    "1.2.03",
    "1.2.3-beta.01",
    "1.2.3-",
    "1.2.3+",
    "1.2.3\n",
    "<html>Sign in</html>",
  ])("rejects invalid release metadata instead of treating it as an update: %s", (version) => {
    expect(() => isNewerDashboardRelease(version, "1.2.3")).toThrow("not valid SemVer");
    expect(() => isNewerDashboardRelease("1.2.3", version)).toThrow("not valid SemVer");
  });
});

describe("dashboard release update monitor", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-01-01T00:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("checks at startup and exactly hourly, not on repeated starts or visibility events", async () => {
    const h = harness();
    h.monitor.dismiss();
    h.monitor.start();
    h.monitor.start();
    h.monitor.setVisible(true);
    await flush();
    expect(h.request).toHaveBeenCalledOnce();
    expect(h.request).toHaveBeenCalledWith(
      "/__mockapi/version.txt",
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        redirect: "error",
        signal: expect.any(AbortSignal),
      })
    );
    expect(h.onAvailable).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(hour - 1);
    expect(h.request).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(1);
    expect(h.request).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(hour);
    expect(h.request).toHaveBeenCalledTimes(3);
    expect(h.onError).not.toHaveBeenCalled();
    h.monitor.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(["1.2.3+another-commit", "1.2.2", "1.2.3-beta.1"])(
    "keeps checking without a notice for a same-release build or older deployment: %s",
    async (version) => {
      const h = harness({ request: vi.fn(async () => new Response(version)) });
      h.monitor.start();
      await flush();
      await vi.advanceTimersByTimeAsync(hour);
      expect(h.onAvailable).not.toHaveBeenCalled();
      expect(h.onError).not.toHaveBeenCalled();
      h.monitor.dispose();
    }
  );

  it("stops every check once a newer release is pending, including after dismissal and resume", async () => {
    const h = harness();
    h.request.mockImplementationOnce(async () => new Response("1.3.0\n"));
    h.monitor.start();
    await flush();
    expect(h.onAvailable).toHaveBeenCalledExactlyOnceWith("1.3.0", true);
    expect(vi.getTimerCount()).toBe(0);
    h.monitor.dismiss();
    expect(h.entries.get(DASHBOARD_UPDATE_DISMISSED_KEY)).toBe("true");
    expect(h.onAvailable).toHaveBeenLastCalledWith("1.3.0", false);
    h.monitor.setVisible(false);
    await vi.advanceTimersByTimeAsync(3 * hour);
    h.monitor.setVisible(true);
    await flush();
    h.monitor.start();
    expect(h.request).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
    h.monitor.dispose();
    h.monitor.dismiss();
    expect(h.onAvailable).toHaveBeenCalledTimes(2);
  });

  it("restores session dismissal after a page reload but keeps the update button available", async () => {
    const h = harness();
    h.entries.set(DASHBOARD_UPDATE_DISMISSED_KEY, "true");
    h.request.mockImplementationOnce(async () => new Response("1.4.0"));
    h.monitor.start();
    await flush();
    expect(h.onAvailable).toHaveBeenCalledExactlyOnceWith("1.4.0", false);
    expect(h.request).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
    h.monitor.dispose();
  });

  it("waits for visibility at startup and does not check early when a hidden tab returns", async () => {
    const h = harness();
    h.monitor.setVisible(false);
    h.monitor.setVisible(true);
    h.monitor.setVisible(false);
    h.monitor.start();
    await vi.advanceTimersByTimeAsync(2 * hour);
    expect(h.request).not.toHaveBeenCalled();
    h.monitor.setVisible(true);
    await flush();
    expect(h.request).toHaveBeenCalledOnce();
    h.monitor.setVisible(false);
    await vi.advanceTimersByTimeAsync(hour / 2);
    h.monitor.setVisible(true);
    await flush();
    expect(h.request).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(hour / 2);
    expect(h.request).toHaveBeenCalledTimes(2);
    h.monitor.setVisible(false);
    await vi.advanceTimersByTimeAsync(2 * hour);
    expect(h.request).toHaveBeenCalledTimes(2);
    h.monitor.setVisible(true);
    await flush();
    expect(h.request).toHaveBeenCalledTimes(3);
    h.monitor.dispose();
  });

  it.each(["hide", "dispose"])(
    "aborts an in-flight read on %s and ignores a late successful response",
    async (action) => {
      let resolve;
      const request = vi.fn(
        () =>
          new Promise((complete) => {
            resolve = complete;
          })
      );
      const h = harness({ request });
      h.monitor.start();
      const signal = request.mock.calls[0][1].signal;
      if (action === "hide") h.monitor.setVisible(false);
      else h.monitor.dispose();
      expect(signal.aborted).toBe(true);
      expect(vi.getTimerCount()).toBe(0);
      resolve(new Response("2.0.0"));
      await flush();
      expect(h.onAvailable).not.toHaveBeenCalled();
      expect(h.onError).not.toHaveBeenCalled();
      h.monitor.dispose();
    }
  );

  it("ignores late failures after disposal and never restarts a disposed monitor", async () => {
    let reject;
    const h = harness({
      request: vi.fn(
        () =>
          new Promise((_, fail) => {
            reject = fail;
          })
      ),
    });
    h.monitor.start();
    h.monitor.dispose();
    reject(new Error("Late failure"));
    await flush();
    h.monitor.start();
    h.monitor.setVisible(false);
    h.monitor.setVisible(true);
    await vi.advanceTimersByTimeAsync(hour);
    expect(h.onError).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each([
    ["HTTP error", () => Promise.resolve(new Response("Unavailable", { status: 503 })), "HTTP 503"],
    ["invalid marker", () => Promise.resolve(new Response("<html>sign in</html>")), "not valid SemVer"],
    ["network failure", () => Promise.reject(new Error("Offline")), "Offline"],
    [
      "body read failure",
      () => Promise.resolve({ ok: true, text: () => Promise.reject(new Error("Read failed")) }),
      "Read failed",
    ],
  ])("reports a %s and retries only after an hour", async (_, failure, message) => {
    const h = harness();
    h.request.mockImplementationOnce(failure);
    h.monitor.start();
    await flush();
    expect(h.onError).toHaveBeenCalledOnce();
    expect(h.onError.mock.calls[0][0].message).toContain(message);
    expect(h.onAvailable).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(hour - 1);
    expect(h.request).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(1);
    expect(h.request).toHaveBeenCalledTimes(2);
    h.monitor.dispose();
  });

  it("bounds a stalled request and retains the hourly retry cadence", async () => {
    const h = harness();
    h.request.mockImplementationOnce(
      (_, { signal }) =>
        new Promise((_, reject) => {
          signal.addEventListener("abort", () => reject(signal.reason), { once: true });
        })
    );
    h.monitor.start();
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.onError.mock.calls[0][0].message).toContain("timed out");
    await vi.advanceTimersByTimeAsync(hour - 15000);
    expect(h.request).toHaveBeenCalledTimes(2);
    h.monitor.dispose();
  });

  it("reports blocked session storage reads and continues with a page-local notice", async () => {
    const h = harness({
      sessionStorage: {
        getItem: () => {
          throw new Error("Blocked");
        },
      },
      request: vi.fn(async () => new Response("1.3.0")),
    });
    h.monitor.start();
    await flush();
    expect(h.onError.mock.calls[0][0].message).toContain("could not be restored");
    expect(h.onAvailable).toHaveBeenCalledExactlyOnceWith("1.3.0", true);
    h.monitor.dispose();
  });

  it("still dismisses the notice if session storage cannot be written", async () => {
    const h = harness({
      sessionStorage: {
        getItem: () => null,
        setItem: () => {
          throw new Error("Blocked");
        },
      },
      request: vi.fn(async () => new Response("1.3.0")),
    });
    h.monitor.start();
    await flush();
    h.monitor.dismiss();
    expect(h.onError.mock.calls[0][0].message).toContain("applies only to this page");
    expect(h.onAvailable).toHaveBeenLastCalledWith("1.3.0", false);
    expect(vi.getTimerCount()).toBe(0);
    h.monitor.dispose();
  });

  it("rejects a missing or invalid embedded version before doing any work", () => {
    expect(() => harness({ currentVersion: "" })).toThrow("not valid SemVer");
  });
});
