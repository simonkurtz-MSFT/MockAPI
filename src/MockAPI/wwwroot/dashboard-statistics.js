"use strict";

import {
  explainEndpointStatistics,
  formatBytes,
  formatNumber,
  formatTime,
  formatUtcTime,
  formatRequestLogResult,
  formatTransportAttemptInsight,
  groupRequestsByMinute,
  hasScrollableStatisticsHistory,
} from "./dashboard-core.js?v={{ASSET_VERSION}}";
import {
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "./dashboard-dom.js?v={{ASSET_VERSION}}";

/**
 * @typedef {object} DashboardStatisticsPanel
 * @property {(endpoints: readonly import("./dashboard-core.js").MockEndpoint[]) => void} updateEndpoints Replaces definitions and reconciles scope/labels by stable ID.
 * @property {(statistics: import("./dashboard-core.js").DashboardStatistics) => void} updateStatistics Replaces process-local activity; collapsed/inactive views render when shown.
 * @property {() => void} dispose Releases static/chart/log listeners and render caches. Stop supplying updates after disposal.
 */
/**
 * Owns statistics scope/view selection, chart and request-log rendering, caches, and bucket navigation.
 * @param {object} options Dependencies.
 * @param {Document} options.documentRoot Dashboard document.
 * @param {import("./dashboard-preferences.js").DashboardPreferencesStore} options.preferencesStore View preferences.
 * @param {(endpoint: import("./dashboard-core.js").MockEndpoint|null) => void} options.onReset Confirms a reset; null selects overall statistics.
 * @returns {DashboardStatisticsPanel} Statistics and request-log owner.
 * @throws {Error} Required dashboard markup is missing or has an incorrect tag.
 */
export function createDashboardStatistics({ documentRoot, preferencesStore, onReset }) {
  const document = documentRoot;
  const window = document.defaultView;
  const elements = getDashboardElements(document, {
    "metric-bytes": "strong",
    "metric-matched": "strong",
    "metric-rate": "strong",
    "metric-total": "strong",
    "metric-unmatched": "strong",
    "request-log-body": "div",
    "request-log-count": "span",
    "request-log-insight": "p",
    "request-log-rows": "tbody",
    "request-log-toggle": "button",
    "request-log-toggle-buckets": "button",
    "reset-statistics-scope": "button",
    "statistics-body": "div",
    "statistics-chart": "div",
    "statistics-chart-scroll": "div",
    "statistics-chart-summary": "span",
    "statistics-chart-timeline": "div",
    "statistics-chart-title": "strong",
    "statistics-controls": "div",
    "statistics-endpoint": "button",
    "statistics-endpoint-picker": "label",
    "statistics-endpoint-select": "select",
    "statistics-graph": "button",
    "statistics-graph-view": "div",
    "statistics-overall": "button",
    "statistics-table": "button",
    "statistics-table-view": "div",
    "statistics-toggle": "button",
  });
  const STATISTICS_RETENTION_MINUTES = 60;
  const STATISTICS_VISIBLE_MINUTES = 30;
  const preferences = preferencesStore.get();
  const events = createDashboardEventScope();
  const chartEvents = createDashboardEventScope();
  const logEvents = createDashboardEventScope();
  const { makeElement } = createDashboardDom(document, events);
  /** @type {readonly import("./dashboard-core.js").MockEndpoint[]|null} */
  let statisticsPickerEndpoints = null;
  /** @type {{endpoints: readonly import("./dashboard-core.js").MockEndpoint[], content: string}|null} */
  let requestLogCache = null;
  /** @type {{
   * endpoints: readonly import("./dashboard-core.js").MockEndpoint[],
   * statistics: import("./dashboard-core.js").DashboardStatistics|null,
   * statisticsByEndpoint: Map<string, import("./dashboard-core.js").EndpointStatistics>,
   * statisticsScope: "overall"|"endpoint", statisticsView: import("./dashboard-preferences.js").StatisticsView,
   * statisticsEndpointId: string|null, statisticsCollapsed: boolean, requestLogCollapsed: boolean,
   * collapsedRequestLogBuckets: Set<string>
   * }} */
  const state = {
    endpoints: [],
    statistics: null,
    statisticsByEndpoint: new Map(),
    statisticsScope: "overall",
    statisticsView: preferences.statisticsView,
    statisticsEndpointId: null,
    statisticsCollapsed: preferences.statisticsCollapsed,
    requestLogCollapsed: preferences.requestLogCollapsed,
    collapsedRequestLogBuckets: new Set(),
  };
  function renderStatistics() {
    const statistics = state.statistics;
    if (!statistics) return;
    elements["metric-total"].textContent = formatNumber(statistics.totalRequests);
    elements["metric-matched"].textContent = formatNumber(statistics.matchedRequests);
    elements["metric-unmatched"].textContent = formatNumber(statistics.unmatchedRequests);
    elements["metric-bytes"].textContent = formatBytes(statistics.responseBytes);
    const recent = statistics.recentMinutes || [];
    const recentTotal = recent.reduce((sum, bucket) => sum + bucket.requests, 0);
    elements["metric-rate"].textContent = `${(recentTotal / 60).toFixed(1)} attempts/min`;
    renderDetailedStatistics();
    renderRequestLog();
  }

  function renderRequestLog() {
    const recentRequests = state.statistics?.recentRequests || [];
    const groups = groupRequestsByMinute(recentRequests);
    const bucketKeys = new Set(groups.map((group) => group.minuteUtc));
    for (const key of state.collapsedRequestLogBuckets) {
      if (!bucketKeys.has(key)) state.collapsedRequestLogBuckets.delete(key);
    }
    elements["request-log-count"].textContent = formatNumber(recentRequests.length);
    updateRequestLogBucketControl();
    if (state.requestLogCollapsed) return;
    const cache = JSON.stringify([recentRequests, [...state.collapsedRequestLogBuckets]]);
    if (requestLogCache?.endpoints === state.endpoints && requestLogCache.content === cache) return;
    logEvents.clear();
    requestLogCache = { endpoints: state.endpoints, content: cache };
    const focusedBucket = document.activeElement?.getAttribute("data-request-log-minute");
    const transportAttemptInsight = formatTransportAttemptInsight(recentRequests);
    elements["request-log-insight"].textContent = transportAttemptInsight;
    elements["request-log-insight"].hidden = !transportAttemptInsight;

    if (recentRequests.length === 0) {
      const row = document.createElement("tr");
      const cell = makeElement("td", "request-log-empty", "No requests recorded");
      cell.colSpan = 8;
      row.append(cell);
      elements["request-log-rows"].replaceChildren(row);
      return;
    }

    const endpointsById = new Map(state.endpoints.map((endpoint) => [endpoint.id, endpoint]));
    const rows = [];
    for (const group of groups) {
      const bucketKey = group.minuteUtc;
      const bucketId = `request-log-bucket-${new Date(bucketKey).getTime()}`;
      const controlledRowIds = group.requests.map((_, requestIndex) => `${bucketId}-${requestIndex}`);
      const collapsed = state.collapsedRequestLogBuckets.has(bucketKey);
      const bucketRow = document.createElement("tr");
      bucketRow.className = "request-log-bucket";
      bucketRow.dataset.requestLogMinute = bucketKey;
      const bucketHeading = document.createElement("td");
      bucketHeading.colSpan = 8;
      const localBucketTime = makeElement("time", "tabular-numeric", formatTime(group.minuteUtc));
      localBucketTime.dateTime = group.minuteUtc;
      const utcBucketTime = makeElement("time", "tabular-numeric", formatUtcTime(group.minuteUtc));
      utcBucketTime.dateTime = group.minuteUtc;
      const toggle = makeElement("button", "request-log-bucket-toggle");
      toggle.type = "button";
      toggle.dataset.requestLogMinute = bucketKey;
      toggle.setAttribute("aria-expanded", String(!collapsed));
      toggle.setAttribute("aria-controls", controlledRowIds.join(" "));
      const toggleIcon = makeElement("span", "request-log-bucket-chevron");
      toggleIcon.setAttribute("aria-hidden", "true");
      const label = makeElement("span", "request-log-bucket-label");
      label.append("Bucket: ", localBucketTime);
      const count = makeElement(
        "span",
        "request-log-bucket-count",
        `${formatNumber(group.requests.length)} ${group.requests.length === 1 ? "request" : "requests"}`
      );
      toggle.append(toggleIcon, label, count);
      logEvents.listen(toggle, "click", () => {
        const nextCollapsed = toggle.getAttribute("aria-expanded") === "true";
        setRequestLogBucketCollapsed(bucketKey, nextCollapsed);
      });
      bucketHeading.append(toggle);
      bucketRow.append(bucketHeading);
      rows.push(bucketRow);

      for (const [requestIndex, request] of group.requests.entries()) {
        const endpoint = request.endpointId ? endpointsById.get(request.endpointId) : null;
        const row = document.createElement("tr");
        row.className = "request-log-entry";
        row.id = controlledRowIds[requestIndex];
        row.dataset.requestLogBucket = bucketKey;
        row.hidden = collapsed;
        const localTime = makeElement("time", "tabular-numeric", formatTime(request.timestampUtc));
        localTime.dateTime = request.timestampUtc;
        const utcTime = makeElement("time", "tabular-numeric", formatUtcTime(request.timestampUtc));
        utcTime.dateTime = request.timestampUtc;
        const result = formatRequestLogResult(request);
        const methodCell = document.createElement("td");
        methodCell.append(makeElement("span", "method-badge", request.method || "—"));
        row.append(
          makeElement("td", null),
          makeElement("td", null),
          methodCell,
          makeElement("td", null, endpoint?.name || (request.endpointId ? "Deleted endpoint" : "No match")),
          makeElement("td", "request-log-path", request.path || "—"),
          makeElement("td", `request-log-result ${result.className}`, result.label),
          makeElement("td", "numeric request-log-attempts", formatNumber(request.transportAttempts || 1)),
          makeElement("td", "numeric", formatNumber(request.responseBytes || 0))
        );
        row.children[0].append(localTime);
        row.children[1].append(utcTime);
        rows.push(row);
      }
    }

    elements["request-log-rows"].replaceChildren(...rows);
    if (focusedBucket) {
      /** @type {HTMLButtonElement|null} */
      const toggle = elements["request-log-rows"].querySelector(
        `.request-log-bucket-toggle[data-request-log-minute="${CSS.escape(focusedBucket)}"]`
      );
      toggle?.focus({ preventScroll: true });
    }
  }

  function setRequestLogBucketCollapsed(bucketKey, collapsed) {
    requestLogCache = null;
    if (collapsed) {
      state.collapsedRequestLogBuckets.add(bucketKey);
    } else {
      state.collapsedRequestLogBuckets.delete(bucketKey);
    }

    const toggle = elements["request-log-rows"].querySelector(
      `.request-log-bucket-toggle[data-request-log-minute="${CSS.escape(bucketKey)}"]`
    );
    toggle?.setAttribute("aria-expanded", String(!collapsed));
    /** @type {NodeListOf<HTMLTableRowElement>} */
    const rows = elements["request-log-rows"].querySelectorAll(
      `.request-log-entry[data-request-log-bucket="${CSS.escape(bucketKey)}"]`
    );
    for (const row of rows) {
      row.hidden = collapsed;
    }
    updateRequestLogBucketControl();
  }

  function updateRequestLogBucketControl() {
    const groups = groupRequestsByMinute(state.statistics?.recentRequests || []);
    const allCollapsed =
      groups.length > 0 && groups.every((group) => state.collapsedRequestLogBuckets.has(group.minuteUtc));
    const control = elements["request-log-toggle-buckets"];
    control.disabled = groups.length === 0;
    control.textContent = allCollapsed ? "Expand all buckets" : "Collapse all buckets";
  }

  function toggleAllRequestLogBuckets() {
    const groups = groupRequestsByMinute(state.statistics?.recentRequests || []);
    const collapse = groups.some((group) => !state.collapsedRequestLogBuckets.has(group.minuteUtc));
    state.collapsedRequestLogBuckets = collapse ? new Set(groups.map((group) => group.minuteUtc)) : new Set();
    renderRequestLog();
  }

  function revealRequestLogBucket(bucketKey) {
    setRequestLogBucketCollapsed(bucketKey, false);
    setRequestLogCollapsed(false);
    preferencesStore.update({ requestLogCollapsed: false });
    renderRequestLog();
    /** @type {HTMLButtonElement|null} */
    const toggle = elements["request-log-rows"].querySelector(
      `.request-log-bucket-toggle[data-request-log-minute="${CSS.escape(bucketKey)}"]`
    );
    if (!toggle) return;
    toggle.scrollIntoView({ block: "center" });
    toggle.focus();
  }

  function renderDetailedStatistics() {
    const endpointMode = state.statisticsScope === "endpoint";
    elements["statistics-overall"].setAttribute("aria-pressed", String(!endpointMode));
    elements["statistics-endpoint"].setAttribute("aria-pressed", String(endpointMode));
    elements["statistics-table"].setAttribute("aria-pressed", String(state.statisticsView === "table"));
    elements["statistics-graph"].setAttribute("aria-pressed", String(state.statisticsView === "graph"));
    elements["statistics-endpoint-picker"].classList.toggle("statistics-endpoint-picker-placeholder", !endpointMode);
    elements["statistics-endpoint-picker"].setAttribute("aria-hidden", String(!endpointMode));
    elements["statistics-table-view"].hidden = state.statisticsView !== "table";
    elements["statistics-graph-view"].hidden = state.statisticsView !== "graph";

    if (!state.statistics || state.statisticsCollapsed) return;
    populateStatisticsEndpointSelect();
    const selectedEndpoint = state.endpoints.find((endpoint) => endpoint.id === state.statisticsEndpointId);
    const selectedStatistics = selectedEndpoint ? endpointStatistics(selectedEndpoint.id) : null;
    elements["reset-statistics-scope"].textContent = endpointMode ? "Reset endpoint" : "Reset statistics";
    elements["reset-statistics-scope"].disabled = endpointMode && !selectedStatistics;

    if (state.statisticsView === "table") renderStatisticsTable(endpointMode);
    else renderStatisticsGraph(endpointMode, selectedEndpoint, selectedStatistics);
  }

  function setStatisticsCollapsed(collapsed) {
    state.statisticsCollapsed = collapsed;
    document.documentElement.dataset.statisticsCollapsed = String(collapsed);
    elements["statistics-body"].hidden = collapsed;
    elements["statistics-controls"].hidden = collapsed;
    elements["statistics-toggle"].setAttribute("aria-expanded", String(!collapsed));
    const label = collapsed ? "Expand statistics" : "Collapse statistics";
    elements["statistics-toggle"].setAttribute("aria-label", label);
    elements["statistics-toggle"].title = label;
    if (!collapsed) renderStatistics();
  }

  function setRequestLogCollapsed(collapsed) {
    state.requestLogCollapsed = collapsed;
    document.documentElement.dataset.requestLogCollapsed = String(collapsed);
    elements["request-log-body"].hidden = collapsed;
    elements["request-log-toggle"].setAttribute("aria-expanded", String(!collapsed));
    const label = collapsed ? "Expand request log" : "Collapse request log";
    elements["request-log-toggle"].setAttribute("aria-label", label);
    elements["request-log-toggle"].title = label;
    if (!collapsed) renderRequestLog();
  }

  function populateStatisticsEndpointSelect() {
    if (!state.endpoints.some((endpoint) => endpoint.id === state.statisticsEndpointId)) {
      state.statisticsEndpointId = state.endpoints[0]?.id || null;
    }
    const select = elements["statistics-endpoint-select"];
    if (statisticsPickerEndpoints === state.endpoints) {
      select.value = state.statisticsEndpointId || "";
      return;
    }
    statisticsPickerEndpoints = state.endpoints;
    const focused = document.activeElement === select;
    select.replaceChildren(
      ...state.endpoints.map((endpoint) => {
        const option = document.createElement("option");
        option.value = endpoint.id;
        option.textContent = `${endpoint.name} · ${endpoint.path}`;
        option.selected = endpoint.id === state.statisticsEndpointId;
        return option;
      })
    );
    select.disabled = state.endpoints.length === 0;
    if (focused) select.focus();
  }

  function renderStatisticsTable(endpointMode) {
    const container = elements["statistics-table-view"];
    const table = document.createElement("table");
    const head = document.createElement("thead");
    const body = document.createElement("tbody");
    const headerRow = document.createElement("tr");

    if (endpointMode) {
      for (const label of ["Endpoint", "Attempts", "Rate", "Last status", "Response bytes", "Why this response"]) {
        const header = makeElement("th", null, label);
        header.scope = "col";
        headerRow.append(header);
        if (["Attempts", "Rate", "Response bytes"].includes(label)) {
          header.classList.add("numeric");
        }
      }
      for (const endpoint of state.endpoints) {
        const statistics = endpointStatistics(endpoint.id);
        const recent = statistics?.recentMinutes || [];
        const row = document.createElement("tr");
        const endpointCell = document.createElement("td");
        endpointCell.append(
          makeElement("strong", null, endpoint.name),
          makeElement("small", "statistics-path", endpoint.path)
        );
        row.append(
          endpointCell,
          makeElement("td", "numeric", formatNumber(statistics?.totalRequests || 0)),
          makeElement("td", "numeric", `${(sumRequests(recent) / 60).toFixed(1)} attempts/min`),
          makeElement("td", "numeric", statistics?.lastStatusCode ? String(statistics.lastStatusCode) : "—"),
          makeElement("td", "numeric", formatBytes(statistics?.responseBytes || 0)),
          makeElement("td", "statistics-explanation", explainEndpointStatistics(endpoint, statistics))
        );
        body.append(row);
      }
    } else {
      for (const label of ["Measure", "Value", "Meaning"]) {
        const header = makeElement("th", null, label);
        header.scope = "col";
        headerRow.append(header);
      }
      const statistics = state.statistics;
      /** @type {[string, number|string, string][]} */
      const rows = [
        ["Total attempts", statistics.totalRequests, "Every transport attempt seen by the mock dispatcher"],
        ["Matched", statistics.matchedRequests, "Transport attempts attributed to a configured endpoint"],
        ["Unmatched", statistics.unmatchedRequests, "Transport attempts that did not match an enabled endpoint"],
        ["Failed writes", statistics.failedWrites, "Matched responses that could not be fully written"],
        ["1xx responses", statistics.informationalResponses, "Informational responses"],
        ["2xx responses", statistics.successResponses, "Successful responses"],
        ["3xx responses", statistics.redirectionResponses, "Redirection responses"],
        ["4xx responses", statistics.clientErrorResponses, "Client-error responses, including configured 429s"],
        ["5xx responses", statistics.serverErrorResponses, "Server-error responses"],
        ["Response bytes", formatBytes(statistics.responseBytes), "Response body bytes written by matched endpoints"],
      ];
      for (const [label, value, meaning] of rows) {
        const row = document.createElement("tr");
        const heading = makeElement("th", null, label);
        heading.scope = "row";
        row.append(heading, makeElement("td", "numeric", String(value)), makeElement("td", null, meaning));
        body.append(row);
      }
    }

    head.append(headerRow);
    table.append(head, body);
    container.replaceChildren(table);
  }

  function renderStatisticsGraph(endpointMode, endpoint, statistics) {
    chartEvents.clear();
    const recent = endpointMode ? statistics?.recentMinutes || [] : state.statistics.recentMinutes || [];
    const total = sumRequests(recent);
    elements["statistics-chart-title"].textContent = endpointMode
      ? endpoint
        ? `${endpoint.name} attempt activity`
        : "Endpoint attempt activity"
      : "Overall attempt activity";
    elements["statistics-chart-summary"].textContent =
      `${formatNumber(total)} attempts · ${(total / 60).toFixed(1)} attempts/min`;
    const chartContext = endpointMode
      ? endpoint
        ? explainEndpointStatistics(endpoint, statistics)
        : "Create or load an endpoint to inspect its attempt activity."
      : `${formatNumber(state.statistics.matchedRequests)} matched and ${formatNumber(state.statistics.unmatchedRequests)} unmatched attempts have been observed.`;

    const chart = elements["statistics-chart"];
    const chartScroll = elements["statistics-chart-scroll"];
    const chartTimeline = elements["statistics-chart-timeline"];
    const wasFollowingNewest =
      chartScroll.dataset.initialized !== "true" ||
      chartScroll.scrollWidth - chartScroll.clientWidth - chartScroll.scrollLeft <= 24;
    const hasOlderActivity = hasScrollableStatisticsHistory(recent, STATISTICS_VISIBLE_MINUTES);
    const timelineWidth = hasOlderActivity ? (STATISTICS_RETENTION_MINUTES / STATISTICS_VISIBLE_MINUTES) * 100 : 100;
    chartTimeline.style.width = `${timelineWidth}%`;
    chartScroll.setAttribute(
      "aria-label",
      hasOlderActivity
        ? "Attempt activity timeline. The newest 30 minutes are shown initially; scroll horizontally for older activity."
        : "Attempt activity timeline."
    );
    const maximum = Math.max(1, ...recent.map((bucket) => bucket.requests));
    const requestLogBuckets = new Set(
      groupRequestsByMinute(state.statistics.recentRequests || []).map((group) => minuteBucketKey(group.minuteUtc))
    );
    const tooltip = createStatisticsTooltip();
    const columns = recent.map((bucket) => {
      const bucketKey = minuteBucketKey(bucket.minuteUtc);
      const hasRequestLogBucket = bucket.requests > 0 && requestLogBuckets.has(bucketKey);
      const column = makeElement(hasRequestLogBucket ? "button" : "i", "chart-column");
      const statusCounts = getMinuteStatusCounts(bucket);
      column.style.height = `${(bucket.requests / maximum) * 100}%`;
      if (bucket.requests > 0) column.classList.add("active");
      if (hasRequestLogBucket) {
        column.setAttribute("type", "button");
        column.classList.add("interactive");
        column.setAttribute(
          "aria-label",
          `Show ${formatNumber(bucket.requests)} ${bucket.requests === 1 ? "attempt" : "attempts"} from ${formatTime(bucket.minuteUtc)} in the request log`
        );
        chartEvents.listen(column, "click", () => revealRequestLogBucket(bucketKey));
      }
      for (const [statusClass, count] of [
        ["status-1xx", statusCounts.informational],
        ["status-2xx", statusCounts.success],
        ["status-3xx", statusCounts.redirection],
        ["status-4xx", statusCounts.clientError],
        ["status-5xx", statusCounts.serverError],
        ["status-other", statusCounts.other],
      ]) {
        const segment = makeElement("span", statusClass);
        segment.style.flexGrow = String(count);
        segment.hidden = count === 0;
        column.append(segment);
      }
      chartEvents.listen(column, "pointerenter", (event) => {
        showStatisticsTooltip(tooltip, bucket, statusCounts, event);
      });
      chartEvents.listen(column, "pointermove", (event) => {
        positionStatisticsTooltip(tooltip, event);
      });
      chartEvents.listen(column, "pointerleave", () => {
        tooltip.hidden = true;
      });
      return column;
    });
    chart.replaceChildren(...columns, tooltip);
    chartScroll.dataset.initialized = "true";
    if (wasFollowingNewest) chartScroll.scrollLeft = chartScroll.scrollWidth;
    const navigationDescription = hasOlderActivity
      ? "The newest 30 minutes are shown initially; scroll horizontally for older activity."
      : "All activity is visible without horizontal scrolling.";
    chart.setAttribute(
      "aria-label",
      `${elements["statistics-chart-title"].textContent}. ${formatNumber(total)} attempts over the last 60 minutes; peak ${formatNumber(maximum)} attempts in one minute. ${navigationDescription} ${chartContext}`
    );
  }

  function createStatisticsTooltip() {
    const tooltip = makeElement("div", "chart-tooltip");
    tooltip.setAttribute("aria-hidden", "true");
    tooltip.hidden = true;
    return tooltip;
  }

  function showStatisticsTooltip(tooltip, bucket, statusCounts, event) {
    const heading = makeElement("div", "chart-tooltip-heading");
    heading.append(
      makeElement("strong", null, formatTime(bucket.minuteUtc)),
      makeElement("span", null, `${formatNumber(bucket.requests)} attempts`)
    );
    const details = makeElement("div", "chart-tooltip-details");
    for (const [statusClass, label, count] of [
      ["status-1xx", "1xx", statusCounts.informational],
      ["status-2xx", "2xx", statusCounts.success],
      ["status-3xx", "3xx", statusCounts.redirection],
      ["status-4xx", "4xx", statusCounts.clientError],
      ["status-5xx", "5xx", statusCounts.serverError],
      ["status-other", "Other", statusCounts.other],
    ]) {
      const row = makeElement("div", "chart-tooltip-row");
      row.append(
        makeElement("i", statusClass),
        makeElement("span", null, label),
        makeElement("strong", "numeric", formatNumber(count))
      );
      details.append(row);
    }
    tooltip.replaceChildren(heading, details);
    tooltip.hidden = false;
    positionStatisticsTooltip(tooltip, event);
  }

  function positionStatisticsTooltip(tooltip, event) {
    const viewportMargin = 12;
    const pointerGap = 14;
    const bounds = tooltip.getBoundingClientRect();
    let left = event.clientX + pointerGap;
    if (left + bounds.width > window.innerWidth - viewportMargin) {
      left = event.clientX - bounds.width - pointerGap;
    }
    const top = Math.min(
      Math.max(viewportMargin, event.clientY - bounds.height / 2),
      window.innerHeight - bounds.height - viewportMargin
    );
    tooltip.style.left = `${Math.max(viewportMargin, left)}px`;
    tooltip.style.top = `${top}px`;
  }

  function getMinuteStatusCounts(bucket) {
    const informational = bucket.informationalResponses || 0;
    const success = bucket.successResponses || 0;
    const redirection = bucket.redirectionResponses || 0;
    const clientError = bucket.clientErrorResponses || 0;
    const serverError = bucket.serverErrorResponses || 0;
    const classified = informational + success + redirection + clientError + serverError;
    return {
      informational,
      success,
      redirection,
      clientError,
      serverError,
      other: Math.max(0, bucket.requests - classified),
    };
  }

  function sumRequests(buckets) {
    return buckets.reduce((sum, bucket) => sum + bucket.requests, 0);
  }

  function minuteBucketKey(timestamp) {
    return new Date(Math.floor(new Date(timestamp).getTime() / 60_000) * 60_000).toISOString();
  }

  function endpointStatistics(id) {
    return state.statisticsByEndpoint.get(id);
  }

  function setStatisticsScope(scope) {
    state.statisticsScope = scope;
    renderDetailedStatistics();
  }

  function setStatisticsView(view) {
    state.statisticsView = view;
    preferencesStore.update({ statisticsView: view });
    renderDetailedStatistics();
  }
  events.listen(elements["statistics-overall"], "click", () => setStatisticsScope("overall"));
  events.listen(elements["statistics-endpoint"], "click", () => setStatisticsScope("endpoint"));
  events.listen(elements["statistics-table"], "click", () => setStatisticsView("table"));
  events.listen(elements["statistics-graph"], "click", () => setStatisticsView("graph"));
  events.listen(elements["statistics-toggle"], "click", () => {
    setStatisticsCollapsed(!state.statisticsCollapsed);
    preferencesStore.update({ statisticsCollapsed: state.statisticsCollapsed });
  });
  events.listen(elements["request-log-toggle"], "click", () => {
    setRequestLogCollapsed(!state.requestLogCollapsed);
    preferencesStore.update({ requestLogCollapsed: state.requestLogCollapsed });
  });
  events.listen(elements["request-log-toggle-buckets"], "click", toggleAllRequestLogBuckets);
  events.listen(elements["statistics-endpoint-select"], "change", (event) => {
    state.statisticsEndpointId = event.currentTarget.value;
    renderDetailedStatistics();
  });
  events.listen(elements["reset-statistics-scope"], "click", () => {
    const endpointMode = state.statisticsScope === "endpoint";
    const endpoint = state.endpoints.find((candidate) => candidate.id === state.statisticsEndpointId);
    if (endpointMode && !endpoint) return;
    onReset(endpointMode ? endpoint : null);
  });
  setStatisticsCollapsed(state.statisticsCollapsed);
  setRequestLogCollapsed(state.requestLogCollapsed);
  return {
    updateEndpoints(endpoints) {
      state.endpoints = endpoints;
      renderDetailedStatistics();
      renderRequestLog();
    },
    updateStatistics(statistics) {
      state.statistics = statistics;
      state.statisticsByEndpoint = new Map(statistics.endpoints.map((endpoint) => [endpoint.endpointId, endpoint]));
      renderStatistics();
    },
    dispose() {
      events.clear();
      chartEvents.clear();
      logEvents.clear();
      requestLogCache = null;
      statisticsPickerEndpoints = null;
    },
  };
}
