"use strict";

/**
 * @typedef {import("./dashboard-core.js").ConfigurationStatus} ConfigurationStatus
 */

/**
 * @typedef {{kind: "completed"}|{kind: "failed", error: unknown}|{kind: "inactive"}} SynchronizationResult
 */

/**
 * @typedef {{includeStatistics?: boolean, forceEndpoints?: boolean}} SynchronizationOptions
 * Statistics default to included; forcing endpoints invalidates the revision-bound in-memory cache.
 */
/**
 * @typedef {{
 * (path: "/configuration", options?: import("./dashboard-management.js").ManagementRequestOptions): Promise<ConfigurationStatus>;
 * (path: "/endpoints", options?: import("./dashboard-management.js").ManagementRequestOptions): Promise<readonly import("./dashboard-core.js").MockEndpoint[]>;
 * (path: "/statistics", options?: import("./dashboard-management.js").ManagementRequestOptions): Promise<import("./dashboard-core.js").DashboardStatistics>;
 * }} DashboardReadClient
 */
/**
 * @typedef {object} DashboardSynchronizer
 * @property {() => Promise<void>} start Starts once; a hidden page waits until visible.
 * @property {(options?: SynchronizationOptions) => Promise<SynchronizationResult>} synchronize Coalesces reads and reports the final read outcome without replaying writes.
 * @property {(visible: boolean) => Promise<void>} setVisible Aborts obsolete reads while hidden and reconnects on return.
 * @property {() => void} dispose Permanently stops the stream, timers, and requests; later calls do not restart it.
 */

/**
 * Creates one lifecycle-owned dashboard stream with recovery-only polling.
 * Configuration and endpoints stay in memory; hidden/disposed pages abort requests.
 * Concurrent refreshes are coalesced and obsolete responses never reach callbacks.
 *
 * @param {object} options Dependencies and callbacks.
 * @param {DashboardReadClient} options.request Management reads; verifies expectedEtag when provided.
 * @param {((path: string) => EventSource)|null} options.createEventSource Stream factory, or null for polling-only browsers.
 * @param {(callback: () => void, delay: number) => ReturnType<typeof globalThis.setTimeout>} [options.schedule] Timer scheduler.
 * @param {(timer: ReturnType<typeof globalThis.setTimeout>) => void} [options.cancel] Timer cancellation.
 * @param {number} [options.pollInterval] Recovery interval in milliseconds.
 * @param {(configuration: ConfigurationStatus) => void} options.onConfiguration Status callback.
 * @param {(endpoints: readonly import("./dashboard-core.js").MockEndpoint[]) => void} options.onEndpoints Read-only endpoint snapshot callback.
 * @param {(statistics: import("./dashboard-core.js").DashboardStatistics) => void} options.onStatistics Statistics snapshot callback.
 * @param {(online: boolean) => void} options.onConnection Connectivity callback.
 * @param {(error: unknown) => void} options.onError Recoverable failure callback.
 * @returns {DashboardSynchronizer} Stream, polling, and revision-cache owner.
 * Refresh outcomes describe the final coalesced read: completed, failed (also reported through onError),
 * or inactive when hidden/disposed. Background recovery remains scheduled after active failures.
 */
export function createDashboardSynchronizer({
  request,
  createEventSource,
  schedule = globalThis.setTimeout.bind(globalThis),
  cancel = globalThis.clearTimeout.bind(globalThis),
  pollInterval = 15000,
  onConfiguration,
  onEndpoints,
  onStatistics,
  onConnection,
  onError,
}) {
  let visible = true;
  let disposed = false;
  let started = false;
  let generation = 0;
  let controller = new AbortController();
  /** @type {EventSource|null} */
  let stream = null;
  let streamHealthy = false;
  /** @type {ReturnType<typeof globalThis.setTimeout>|null} */
  let timer = null;
  /** @type {ConfigurationStatus|null} */
  let configuration = null;
  /** @type {ConfigurationStatus|null} */
  let publishedConfiguration = null;
  let configurationSequence = 0;
  let statisticsSequence = 0;
  /** @type {number|null} */
  let endpointsRevision = null;
  /** @type {Promise<void>|null} */
  let endpointWork = null;
  /** @type {Promise<SynchronizationResult>|null} */
  let refreshWork = null;
  let refreshAgain = false;
  let statisticsRequested = false;

  const active = (epoch) => visible && !disposed && generation === epoch;

  function stopPolling() {
    if (timer !== null) cancel(timer);
    timer = null;
  }

  function schedulePoll() {
    if (!visible || disposed || timer !== null) return;
    timer = schedule(async () => {
      timer = null;
      await synchronize();
      if (!streamHealthy) schedulePoll();
    }, pollInterval);
  }

  function fail(error, epoch) {
    if (!active(epoch)) return;
    onError(error);
    onConnection(false);
    schedulePoll();
  }

  function updateEndpoints() {
    if (endpointWork) return endpointWork;
    const epoch = generation;
    const signal = controller.signal;
    endpointWork = (async () => {
      while (active(epoch) && configuration && endpointsRevision !== configuration.revision) {
        const target = configuration;
        const sequence = configurationSequence;
        try {
          const endpoints = await request("/endpoints", { signal, expectedEtag: target.etag });
          if (!active(epoch)) return;
          if (configurationSequence !== sequence) continue;
          endpointsRevision = target.revision;
          onEndpoints(endpoints);
        } catch (error) {
          if (!active(epoch)) return;
          if (configurationSequence !== sequence) continue;
          throw error;
        }
      }
    })().finally(() => {
      if (generation === epoch) endpointWork = null;
    });
    return endpointWork;
  }

  /** @param {ConfigurationStatus} next Status received from the management API or stream. */
  function applyConfiguration(next) {
    if (
      !Number.isSafeInteger(next?.revision) ||
      typeof next.etag !== "string" ||
      typeof next.hasUnsavedChanges !== "boolean"
    ) {
      throw new Error("Invalid dashboard configuration event.");
    }
    configuration = next;
    const epoch = generation;
    return updateEndpoints().then(() => {
      if (!active(epoch) || endpointsRevision !== configuration.revision) return;
      const changed =
        !publishedConfiguration ||
        publishedConfiguration.revision !== configuration.revision ||
        publishedConfiguration.hasUnsavedChanges !== configuration.hasUnsavedChanges;
      if (changed) {
        // Do not give old endpoint rows a newer write ETag before their definitions arrive.
        publishedConfiguration = configuration;
        onConfiguration(configuration);
      }
    });
  }

  /** @type {DashboardSynchronizer["synchronize"]} */
  function synchronize({ includeStatistics = true, forceEndpoints = false } = {}) {
    if (!visible || disposed) return Promise.resolve({ kind: "inactive" });
    statisticsRequested ||= includeStatistics;
    if (forceEndpoints) endpointsRevision = null;
    if (refreshWork) {
      refreshAgain = true;
      return refreshWork;
    }
    const epoch = generation;
    const signal = controller.signal;
    refreshWork = (
      /** @returns {Promise<SynchronizationResult>} */ async () => {
        /** @type {SynchronizationResult} */
        let result;
        do {
          refreshAgain = false;
          const fetchStatistics = statisticsRequested;
          statisticsRequested = false;
          const sequence = configurationSequence;
          const statsSequence = statisticsSequence;
          try {
            const next = await request("/configuration", { signal });
            if (!active(epoch)) return { kind: "inactive" };
            if (sequence === configurationSequence) await applyConfiguration(next);
            if (!active(epoch)) return { kind: "inactive" };
            if (fetchStatistics) {
              const statistics = await request("/statistics", { signal });
              if (!active(epoch)) return { kind: "inactive" };
              if (statsSequence === statisticsSequence) onStatistics(statistics);
            }
            onConnection(true);
            result = { kind: "completed" };
          } catch (error) {
            fail(error, epoch);
            if (!active(epoch)) return { kind: "inactive" };
            result = { kind: "failed", error };
          }
        } while (refreshAgain && active(epoch));
        return result;
      }
    )().finally(() => {
      if (generation === epoch) refreshWork = null;
    });
    return refreshWork;
  }

  function activate() {
    if (!visible || disposed || !started || stream) return;
    const epoch = generation;
    if (!createEventSource) {
      return synchronize().then(schedulePoll);
    }
    stream = createEventSource("/dashboard/events");
    stream.onopen = () => {
      if (!active(epoch)) return;
      // Reconnect can reach a restarted process with the same numeric revision.
      endpointsRevision = null;
      configurationSequence++;
    };
    stream.addEventListener("configuration", (event) => {
      if (!active(epoch)) return;
      try {
        configurationSequence++;
        const work = applyConfiguration(JSON.parse(event.data));
        streamHealthy = true;
        stopPolling();
        onConnection(true);
        work.catch((error) => fail(error, epoch));
      } catch (error) {
        fail(error, epoch);
      }
    });
    stream.addEventListener("statistics", (event) => {
      if (!active(epoch)) return;
      try {
        const statistics = JSON.parse(event.data);
        statisticsSequence++;
        onStatistics(statistics);
      } catch (error) {
        fail(error, epoch);
      }
    });
    stream.onerror = () => {
      if (!active(epoch)) return;
      streamHealthy = false;
      fail(new Error("Dashboard event stream disconnected; retrying with polling fallback."), epoch);
    };
    // Also covers proxies that accept a connection but never deliver an initial event.
    schedulePoll();
  }

  function deactivate() {
    generation++;
    controller.abort();
    controller = new AbortController();
    stopPolling();
    stream?.close();
    stream = null;
    streamHealthy = false;
    endpointWork = null;
    refreshWork = null;
    refreshAgain = false;
    statisticsRequested = false;
  }

  async function start() {
    if (started || disposed) return;
    started = true;
    await activate();
  }

  async function setVisible(nextVisible) {
    if (visible === nextVisible || disposed) return;
    visible = nextVisible;
    if (!visible) deactivate();
    else await activate();
  }

  function dispose() {
    if (disposed) return;
    disposed = true;
    deactivate();
  }

  return { start, synchronize, setVisible, dispose };
}
