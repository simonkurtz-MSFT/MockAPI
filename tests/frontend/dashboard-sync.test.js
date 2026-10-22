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
const fingerprint = (id, hash = "A".repeat(32)) => ({ id, contentHash: hash });
const manifest = (revision) => [fingerprint(`endpoint-${revision}`)];
const flush = async () => {
  await vi.advanceTimersByTimeAsync(0);
};

function harness({ eventSource = true, ...options } = {}) {
  const streams = [];
  const callbacks = Object.fromEntries(
    ["configuration", "endpoints", "statistics", "connection", "error"].map((name) => [name, vi.fn()])
  );
  let current = status();
  const request = vi.fn(async (path, options) => {
    if (path === "/configuration") return current;
    if (path === "/endpoints/manifest") return manifest(current.revision);
    if (path === "/endpoints/batch") return JSON.parse(options.body).endpoints.map(({ id }) => ({ id }));
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
            emit: (name, data, lastEventId = "") => listeners.get(name)({ data: JSON.stringify(data), lastEventId }),
            raw: (name, data, lastEventId = "") => listeners.get(name)({ data, lastEventId }),
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

  const statisticsBaseline = () => ({
    totalRequests: 0,
    recentMinutes: Array.from({ length: 60 }, (_, index) => ({
      minuteUtc: new Date(Date.UTC(2026, 0, 1, 0, index)).toISOString(),
      requests: 0,
    })),
    endpoints: [],
    recentRequests: [],
  });
  const statisticsDelta = (changes = {}) => ({
    baseSequence: 1,
    sequence: 2,
    windowStartUtc: "2026-01-01T00:00:00Z",
    totals: { totalRequests: 3 },
    recentMinutes: [],
    endpoints: [],
    removedEndpointIds: [],
    recentRequests: null,
    ...changes,
  });
  async function connectDeltas() {
    const h = harness({ useStatisticsDeltas: true });
    await h.synchronizer.start();
    const stream = h.streams[0];
    stream.onopen();
    stream.emit("configuration", status());
    stream.emit("statistics", statisticsBaseline(), "1");
    await flush();
    return h;
  }

  it("opts into deltas, ignores duplicates, and retains a stream baseline independent from GET refreshes", async () => {
    const h = await connectDeltas();
    const stream = h.streams[0];
    expect(stream.path).toBe("/dashboard/events?deltas=true");
    await h.synchronizer.synchronize();
    stream.emit("statistics-delta", statisticsDelta(), "2");
    expect(h.callbacks.statistics).toHaveBeenLastCalledWith(
      expect.objectContaining({ totalRequests: 3, recentMinutes: expect.any(Array) })
    );
    const calls = h.callbacks.statistics.mock.calls.length;
    stream.emit("statistics-delta", statisticsDelta(), "2");
    stream.emit("statistics-delta", statisticsDelta({ sequence: 1 }), "1");
    expect(h.callbacks.statistics).toHaveBeenCalledTimes(calls);
    stream.onopen();
    stream.emit("statistics", statisticsBaseline(), "1");
    stream.emit("statistics-delta", statisticsDelta(), "2");
    expect(h.callbacks.statistics).toHaveBeenCalledTimes(calls + 2);
    h.synchronizer.dispose();
    stream.emit("statistics-delta", statisticsDelta({ baseSequence: 2, sequence: 3 }), "3");
    expect(h.callbacks.statistics).toHaveBeenCalledTimes(calls + 2);
  });

  it("recovers sequence gaps without aborting a command refresh or accepting retired-stream events", async () => {
    const h = await connectDeltas();
    const old = h.streams[0];
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    const refreshing = h.synchronizer.synchronize();
    old.emit("statistics-delta", statisticsDelta({ baseSequence: 2, sequence: 3 }), "3");
    expect(old.close).toHaveBeenCalledOnce();
    expect(h.callbacks.error).toHaveBeenCalledWith(
      expect.objectContaining({ message: expect.stringContaining("missed") })
    );
    expect(h.streams).toHaveLength(1);
    old.onopen();
    old.emit("configuration", status(99));
    old.emit("statistics", statisticsBaseline(), "99");
    old.onerror();
    pending.resolve(status());
    expect(await refreshing).toEqual({ kind: "completed" });
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.streams).toHaveLength(2);
    h.streams[1].onopen();
    h.streams[1].emit("configuration", status());
    h.streams[1].emit("statistics", statisticsBaseline(), "1");
    await flush();
    expect(vi.getTimerCount()).toBe(0);
    h.synchronizer.dispose();
  });

  it.each(["", "0", "1.5", "9007199254740992"])("rejects invalid baseline sequence %s", async (id) => {
    const h = harness({ useStatisticsDeltas: true });
    await h.synchronizer.start();
    h.streams[0].emit("statistics", statisticsBaseline(), id);
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    expect(h.streams[0].close).toHaveBeenCalledOnce();
    expect(h.callbacks.statistics).not.toHaveBeenCalled();
    h.synchronizer.dispose();
  });

  it.each([
    [statisticsDelta({ sequence: 2.5 }), "2.5"],
    [statisticsDelta(), "9"],
    [statisticsDelta({ baseSequence: 0 }), "2"],
    [statisticsDelta({ windowStartUtc: "invalid" }), "2"],
  ])("reports malformed or discontinuous delta frames", async (delta, id) => {
    const h = await connectDeltas();
    h.streams[0].emit("statistics-delta", delta, id);
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    expect(h.streams[0].close).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(1);
    h.synchronizer.dispose();
  });

  it("rejects deltas before a baseline and malformed JSON after a baseline", async () => {
    const h = harness({ useStatisticsDeltas: true });
    await h.synchronizer.start();
    h.streams[0].emit("statistics-delta", statisticsDelta(), "2");
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(15000);
    h.streams[1].emit("statistics", statisticsBaseline(), "1");
    h.streams[1].raw("statistics-delta", "{", "2");
    expect(h.callbacks.error).toHaveBeenCalledTimes(2);
    h.synchronizer.dispose();
  });

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

  it("makes zero configuration/statistics GETs and only one manifest/batch pair during a healthy idle minute", async () => {
    const h = harness();
    await h.connect();
    await h.synchronizer.start();
    expect(h.streams).toHaveLength(1);
    expect(h.streams[0].path).toBe("/dashboard/events");
    await vi.advanceTimersByTimeAsync(60000);
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/endpoints/manifest", "/endpoints/batch"]);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("applies edits but handles save-only notifications without refetching endpoints", async () => {
    const h = harness();
    await h.connect(status(1, true));
    h.streams[0].emit("configuration", status(1));
    h.streams[0].emit("configuration", status(1));
    await flush();
    expect(h.callbacks.configuration).toHaveBeenCalledTimes(2);
    expect(h.request).toHaveBeenCalledTimes(2);
    h.setStatus(status(2));
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-2" }]);
  });

  it("fetches only changed and added definitions, removes deleted entries, and follows manifest order", async () => {
    const h = harness();
    await h.connect();
    const unchanged = h.callbacks.endpoints.mock.calls[0][0][0];
    h.request.mockClear();
    h.request.mockResolvedValueOnce([fingerprint("added"), fingerprint("endpoint-1")]);
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.request.mock.calls[1]).toEqual([
      "/endpoints/batch",
      expect.objectContaining({
        method: "POST",
        expectedEtag: '"2"',
        body: JSON.stringify({ endpoints: [fingerprint("added")] }),
      }),
    ]);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "added" }, unchanged]);
    expect(h.callbacks.endpoints.mock.lastCall[0][1]).toBe(unchanged);

    h.request.mockClear();
    h.request.mockResolvedValueOnce([fingerprint("endpoint-1", "B".repeat(32))]);
    h.streams[0].emit("configuration", status(3));
    await flush();
    expect(JSON.parse(h.request.mock.calls[1][1].body)).toEqual({
      endpoints: [fingerprint("endpoint-1", "B".repeat(32))],
    });
    expect(h.callbacks.endpoints.mock.lastCall[0]).toEqual([{ id: "endpoint-1" }]);
    expect(h.callbacks.endpoints.mock.lastCall[0][0]).not.toBe(unchanged);

    h.request.mockClear();
    h.request.mockResolvedValueOnce([]);
    h.streams[0].emit("configuration", status(4));
    await flush();
    expect(h.request).toHaveBeenCalledTimes(1);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([]);
    h.synchronizer.dispose();
  });

  it("reuses unchanged definitions for metadata edits and reordering without a batch request", async () => {
    const h = harness();
    await h.connect();
    const cached = h.callbacks.endpoints.mock.lastCall[0][0];
    h.request.mockClear();
    h.request.mockResolvedValueOnce(manifest(1));
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.request).toHaveBeenCalledTimes(1);
    expect(h.callbacks.endpoints.mock.lastCall[0][0]).toBe(cached);
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(2));
    h.synchronizer.dispose();
  });

  it.each([
    null,
    [{}],
    [fingerprint("")],
    [fingerprint("endpoint-1", "short")],
    [fingerprint("same"), fingerprint("same")],
  ])("reports an invalid manifest without replacing the cached snapshot (%j)", async (invalid) => {
    const h = harness();
    await h.connect();
    h.request.mockResolvedValueOnce(invalid);
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.callbacks.error).toHaveBeenCalledOnce();
    expect(h.callbacks.endpoints).toHaveBeenCalledTimes(1);
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(1));
    h.synchronizer.dispose();
  });

  it.each([null, [], [{ id: "unknown" }], [{ id: "added" }, { id: "added" }]])(
    "rejects incomplete or invalid batches without publishing partial state (%j)",
    async (batch) => {
      const h = harness();
      await h.connect();
      h.request.mockResolvedValueOnce([fingerprint("added"), fingerprint("other")]).mockResolvedValueOnce(batch);
      h.streams[0].emit("configuration", status(2));
      await flush();
      expect(h.callbacks.error).toHaveBeenCalledOnce();
      expect(h.callbacks.endpoints).toHaveBeenCalledTimes(1);
      expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(1));
      h.synchronizer.dispose();
    }
  );

  it("recovers a revision conflict without publishing or caching any partial batch", async () => {
    const h = harness();
    await h.connect();
    h.request
      .mockResolvedValueOnce(manifest(2))
      .mockRejectedValueOnce(Object.assign(new Error("Configuration revision conflict"), { status: 412 }));
    h.setStatus(status(2));
    h.streams[0].emit("configuration", status(2));
    await flush();
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(1));
    expect(h.callbacks.endpoints).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-2" }]);
    expect(h.callbacks.configuration).toHaveBeenLastCalledWith(status(2));
    expect(h.request.mock.calls.filter(([path]) => path === "/endpoints/batch")).toHaveLength(3);
    h.synchronizer.dispose();
  });

  it("discards a superseded batch and rebuilds from the last complete cache", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request.mockResolvedValueOnce(manifest(2)).mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2));
    await flush();
    h.setStatus(status(3));
    h.streams[0].emit("configuration", status(3));
    pending.resolve([{ id: "endpoint-2" }]);
    await flush();
    expect(h.callbacks.endpoints).toHaveBeenCalledTimes(2);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-3" }]);
    h.synchronizer.dispose();
  });

  it("honors forced invalidation during an in-flight batch without reusing cleared definitions", async () => {
    const h = harness();
    await h.connect();
    const pending = deferred();
    h.request
      .mockResolvedValueOnce([fingerprint("endpoint-1"), fingerprint("added")])
      .mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2));
    await flush();
    const refresh = h.synchronizer.synchronize({ forceEndpoints: true, includeStatistics: false });
    pending.resolve([{ id: "added" }]);
    expect(await refresh).toEqual({ kind: "completed" });
    expect(h.callbacks.error).not.toHaveBeenCalled();
    expect(h.callbacks.endpoints).toHaveBeenCalledTimes(2);
    expect(h.callbacks.endpoints).toHaveBeenLastCalledWith([{ id: "endpoint-1" }]);
    expect(h.request.mock.calls.filter(([path]) => path === "/endpoints/batch")).toHaveLength(3);
    h.synchronizer.dispose();
  });

  it("accepts an in-flight endpoint response across a save-only notification", async () => {
    const h = harness();
    await h.connect();
    h.request.mockClear();
    const pending = deferred();
    h.request.mockReturnValueOnce(pending.promise);
    h.streams[0].emit("configuration", status(2, true));
    h.streams[0].emit("configuration", status(2));
    pending.resolve(manifest(2));
    await flush();
    expect(h.request.mock.calls.map(([path]) => path)).toEqual(["/endpoints/manifest", "/endpoints/batch"]);
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
    expect(h.request.mock.calls.map(([path]) => path)).toEqual([
      "/endpoints/manifest",
      "/endpoints/batch",
      "/configuration",
      "/statistics",
    ]);
    h.streams[0].onopen();
    h.streams[0].emit("configuration", status());
    await flush();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("recovers from a connection that never delivers an initial event", async () => {
    const h = harness();
    await h.synchronizer.start();
    await vi.advanceTimersByTimeAsync(15000);
    expect(h.request.mock.calls.map(([path]) => path)).toEqual([
      "/configuration",
      "/endpoints/manifest",
      "/endpoints/batch",
      "/statistics",
    ]);
    expect(vi.getTimerCount()).toBe(1);
  });

  it("polls without SSE and preserves the endpoint cache between polls", async () => {
    const h = harness({ eventSource: false });
    await h.synchronizer.start();
    await vi.advanceTimersByTimeAsync(30000);
    expect(h.request.mock.calls.filter(([path]) => path === "/configuration")).toHaveLength(3);
    expect(h.request.mock.calls.filter(([path]) => path === "/endpoints/manifest")).toHaveLength(1);
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
    expect(h.request.mock.calls.map(([path]) => path)).toEqual([
      "/configuration",
      "/configuration",
      "/endpoints/manifest",
      "/endpoints/batch",
    ]);
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
    pending.resolve(manifest(2));
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
    expect(h.request.mock.calls.map(([path]) => path)).toEqual([
      "/endpoints/manifest",
      "/endpoints/batch",
      "/endpoints/manifest",
    ]);
  });

  it.each(["configuration", "endpoints/manifest", "endpoints/batch", "statistics"])(
    "aborts %s requests and ignores late responses when hidden",
    async (path) => {
      const h = harness();
      await h.connect();
      const pending = deferred();
      h.request.mockImplementation(async (url) => {
        if (url === `/${path}`) return pending.promise;
        if (url === "/endpoints/manifest") return manifest(2);
        if (url === "/endpoints/batch") return [{ id: "endpoint-2" }];
        return status(2);
      });
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
