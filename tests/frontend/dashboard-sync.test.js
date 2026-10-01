import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createDashboardSynchronizer } from "../../src/MockAPI/wwwroot/dashboard-sync.js";

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}

const status = (revision = 1, dirty = false) => ({
  revision,
  etag: `"${revision}"`,
  hasUnsavedChanges: dirty,
});
const flush = async () => {
  await vi.advanceTimersByTimeAsync(0);
};

function harness({ eventSource = true, ...options } = {}) {
  const streams = [];
  const callbacks = Object.fromEntries(
    ["configuration", "endpoints", "statistics", "connection", "error"].map((name) => [name, vi.fn()])
  );
  let current = status();
  const request = vi.fn(async (path) => {
    if (path === "/configuration") return current;
    if (path === "/endpoints") return [{ id: `endpoint-${current.revision}` }];
    return { totalRequests: current.revision };
  });
  const synchronizer = createDashboardSynchronizer({
    request,
    createEventSource: eventSource
      ? (path) => {
          const listeners = new Map();
          const stream = {
            path,
            close: vi.fn(),
            addEventListener: (name, listener) => listeners.set(name, listener),
            emit: (name, data) => listeners.get(name)({ data: JSON.stringify(data) }),
            raw: (name, data) => listeners.get(name)({ data }),
          };
          streams.push(stream);
          return stream;
        }
      : null,
    onConfiguration: callbacks.configuration,
    onEndpoints: callbacks.endpoints,
    onStatistics: callbacks.statistics,
    onConnection: callbacks.connection,
    onError: callbacks.error,
    ...options,
  });
  const connect = async (configuration = current) => {
    await synchronizer.start();
    streams.at(-1).onopen();
    streams.at(-1).emit("configuration", configuration);
    streams.at(-1).emit("statistics", { totalRequests: 0 });
    await flush();
  };
  return {
    synchronizer,
    streams,
    request,
    callbacks,
    connect,
    setStatus: (value) => {
      current = value;
    },
  };
}

describe("event-driven dashboard synchronization", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("returns explicit success and failure outcomes while retaining background recovery", async () => {
    const h = harness();
    await h.connect();
    const error = new Error("Refresh unavailable");
    h.request.mockRejectedValueOnce(error);
    expect(await h.synchronizer.synchronize()).toEqual({ kind: "failed", error });
    expect(h.callbacks.error).toHaveBeenCalledWith(error);
    expect(h.callbacks.connection).toHaveBeenLastCalledWith(false);
    expect(vi.getTimerCount()).toBe(1);
    expect(await h.synchronizer.synchronize()).toEqual({ kind: "completed" });
    expect(h.callbacks.connection).toHaveBeenLastCalledWith(true);
    h.synchronizer.dispose();
  });

  it("reports inactive refreshes before starting and after a pending read loses visibility", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    const refresh = h.synchronizer.synchronize();
    await h.synchronizer.setVisible(false);
    pending.resolve(status());
    expect(await refresh).toEqual({ kind: "inactive" });
    expect(await h.synchronizer.synchronize()).toEqual({ kind: "inactive" });
    expect(h.callbacks.error).not.toHaveBeenCalled();
    h.synchronizer.dispose();
    expect(await h.synchronizer.synchronize()).toEqual({ kind: "inactive" });
  });

  it("returns the final coalesced outcome when a follow-up recovers from the first failed read", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    const first = h.synchronizer.synchronize();
    const second = h.synchronizer.synchronize();
    expect(second).toBe(first);
    pending.reject(new Error("First read failed"));
    expect(await second).toEqual({ kind: "completed" });
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    expect(h.request.mock.calls.filter(([path]) => path === "/configuration")).toHaveLength(2);
    h.synchronizer.dispose();
  });

  it("reports failure when the final coalesced read fails after an earlier success", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    const error = new Error("Final read failed");
    h.request.mockReturnValueOnce(pending.promise).mockRejectedValueOnce(error);
    const first = h.synchronizer.synchronize({ includeStatistics: false });
    const second = h.synchronizer.synchronize({ includeStatistics: false });
    pending.resolve(status());
    expect(await first).toEqual({ kind: "failed", error });
    expect(await second).toEqual({ kind: "failed", error });
    h.synchronizer.dispose();
  });

  it("makes zero configuration/statistics GETs and only one endpoint GET during a healthy idle minute", async () => {
    const h = harness();
    await h.connect();
    await h.synchronizer.start();
    expect(h.streams).toHaveLength(1);
    expect(h.streams[0].path).toBe("/dashboard/events");
    await vi.advanceTimersByTimeAsync(60000);
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/endpoints"]);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("applies edits but handles save-only notifications without refetching endpoints", async () => {
    const h = harness();
    await h.connect(status(1, true));
    h.streams[0].emit("configuration", status(1));
    h.streams[0].emit("configuration", status(1));
    await flush();
    expect(h.callbacks.configuration).toHaveBeenCalledTimes(2);
    expect(h.request).toHaveBeenCalledTimes(1);
    h.setStatus(status(2));
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-2" }]);
  });

  it("accepts an in-flight endpoint response across a save-only notification", async () => {
    const h = harness();
    await h.connect();
    h.request.mockClear();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2, true));
    h.streams[0].emit("configuration", status(2));
    pending.resolve([{ id: "endpoint-2" }]);
    await flush();
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/endpoints"]);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-2" }]);
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(2));
  });

  it("uses one completion-scheduled fallback and stops it on stream recovery", async () => {
    const h = harness();
    await h.connect();
    h.streams[0].onerror();
    h.streams[0].onerror();
    expect(vi.getTimerCount()).toBe(1);
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/endpoints", "/configuration", "/statistics"]);
    h.streams[0].onopen();
    h.streams[0].emit("configuration", status());
    await flush();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("recovers from a connection that never delivers an initial event", async () => {
    const h = harness();
    await h.synchronizer.start();
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/configuration", "/endpoints", "/statistics"]);
    expect(vi.getTimerCount()).toBe(1);
  });

  it("polls without SSE and preserves the endpoint cache between polls", async () => {
    const h = harness({ eventSource: false });
    await h.synchronizer.start();
    await vi.advanceTimersByTimeAsync(30000);
    expect(h.request.mock.calls.filter(([path]) => path === "/configuration")).toHaveLength(3);
    expect(h.request.mock.calls.filter(([path]) => path === "/endpoints")).toHaveLength(1);
    h.synchronizer.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("does not initialize hidden pages and reconnects on return", async () => {
    const h = harness();
    await h.synchronizer.setVisible(false);
    await h.synchronizer.setVisible(true);
    expect(h.streams).toHaveLength(0);
    await h.synchronizer.setVisible(false);
    await h.synchronizer.start();
    expect(h.request).not.toHaveBeenCalled();
    await h.synchronizer.synchronize();
    await h.synchronizer.setVisible(true);
    expect(h.streams).toHaveLength(1);
    await h.synchronizer.setVisible(false);
    await h.synchronizer.setVisible(false);
    expect(h.streams[0].close).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(60000);
    expect(h.request).not.toHaveBeenCalled();
    h.synchronizer.dispose();
    h.synchronizer.dispose();
    await h.synchronizer.setVisible(true);
    await h.synchronizer.start();
    expect(h.streams).toHaveLength(1);
  });

  it("ignores late stream callbacks from a hidden generation", async () => {
    const h = harness();
    await h.connect();
    await h.synchronizer.setVisible(false);
    const old = h.streams[0];
    old.onopen();
    old.emit("configuration", status(5));
    old.emit("statistics", { totalRequests: 5 });
    old.onerror();
    expect(h.callbacks.configuration).toHaveBeenCalledTimes(1);
    expect(h.callbacks.statistics).toHaveBeenCalledTimes(1);
    expect(h.callbacks.error).not.toHaveBeenCalled();
    await h.synchronizer.setVisible(true);
    expect(h.streams).toHaveLength(2);
  });

  it.each([
    ["configuration", "not JSON"],
    ["configuration", '{"revision":1}'],
    ["configuration", '{"revision":1,"etag":"1"}'],
    ["configuration", "null"],
    ["statistics", "not JSON"],
  ])("reports malformed %s data and schedules recovery", async (name, data) => {
    const h = harness();
    await h.connect();
    h.streams[0].raw(name, data);
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(1);
  });

  it("supports configuration-only refreshes and explicitly forced endpoint reloads", async () => {
    const h = harness();
    await h.connect();
    h.request.mockClear();
    await h.synchronizer.synchronize({ includeStatistics: false });
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/configuration"]);
    await h.synchronizer.synchronize({ includeStatistics: false, forceEndpoints: true });
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/configuration", "/configuration", "/endpoints"]);
  });

  it("coalesces overlapping refreshes into one follow-up, retaining requested statistics", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    const refresh = h.synchronizer.synchronize({ includeStatistics: false });
    const second = h.synchronizer.synchronize();
    const third = h.synchronizer.synchronize({ includeStatistics: false });
    expect(second).toBe(refresh);
    expect(third).toBe(refresh);
    pending.resolve(status());
    await refresh;
    expect(h.request.mock.calls.filter(([path]) => path === "/configuration")).toHaveLength(2);
    expect(h.request.mock.calls.filter(([path]) => path === "/statistics")).toHaveLength(1);
  });

  it("does not let old HTTP status or statistics overwrite newer events", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    const refresh = h.synchronizer.synchronize();
    h.setStatus(status(3));
    h.streams[0].emit("configuration", status(3));
    h.streams[0].emit("statistics", { totalRequests: 30 });
    pending.resolve(status(2));
    await refresh;
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(3));
    expect(h.callbacks.statistics).toHaveBeenLastCalledWith({ totalRequests: 30 });
  });

  it.each([false, true])("discards superseded endpoint responses including failures (%s)", async (reject) => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2));
    h.setStatus(status(3));
    h.streams[0].emit("configuration", status(3));
    if (reject) pending.reject(new Error("obsolete"));
    else pending.resolve([{ id: "obsolete" }]);
    await flush();
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-3" }]);
    expect(h.callbacks.endpoints).toHaveBeenCalledTimes(2);
    expect(h.callbacks.error).not.toHaveBeenCalled();
  });

  it("retries failed endpoint loading even if the configuration revision has not changed", async () => {
    const h = harness();
    await h.connect();
    h.request.mockRejectedValueOnce(new Error("endpoint failure"));
    h.setStatus(status(2));
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(1));
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-2" }]);
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(2));
  });

  it("publishes a new write ETag only after matching endpoint definitions have arrived", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2));
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(1));
    pending.resolve([{ id: "endpoint-2" }]);
    await flush();
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(2));
    expect(h.callbacks.endpoints.mock.invocationCallOrder.at(-1)).toBeLessThan(
      h.callbacks.configuration.mock.invocationCallOrder.at(-1)
    );
  });

  it("invalidates endpoints on reconnect even when a restarted process reuses its revision", async () => {
    const h = harness();
    await h.connect();
    h.streams[0].onopen();
    h.streams[0].emit("configuration", status());
    await flush();
    expect(h.request).toHaveBeenCalledTimes(2);
  });

  it.each(["configuration", "endpoints", "statistics"])(
    "aborts %s requests and ignores late responses when hidden",
    async (path) => {
      const h = harness();
      await h.connect();
      const pending = deferred();
      h.request.mockImplementation(async (url) => (url === `/${path}` ? pending.promise : status(2)));
      const refresh = h.synchronizer.synchronize({ forceEndpoints: true });
      await flush();
      const options = h.request.mock.calls.at(-1)[1];
      await h.synchronizer.setVisible(false);
      expect(options.signal.aborted).toBe(true);
      pending.resolve(status(2));
      await refresh;
      expect(h.callbacks.statistics).toHaveBeenCalledTimes(1);
      expect(vi.getTimerCount()).toBe(0);
    }
  );

  it("does not report a late rejection or reschedule after disposal", async () => {
    const h = harness({ eventSource: false });
    await h.synchronizer.start();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    const refresh = h.synchronizer.synchronize();
    h.synchronizer.dispose();
    pending.reject(new Error("aborted"));
    await refresh;
    expect(h.callbacks.error).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("handles an endpoint rejection after hiding without reporting an obsolete error", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2));
    await h.synchronizer.setVisible(false);
    pending.reject(new Error("aborted"));
    await flush();
    expect(h.callbacks.error).not.toHaveBeenCalled();
  });

  it("reports HTTP failures and continues polling", async () => {
    const h = harness({ eventSource: false });
    h.request.mockRejectedValueOnce(new Error("offline"));
    await h.synchronizer.start();
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.callbacks.connection).toHaveBeenLastCalledWith(true);
  });
});
