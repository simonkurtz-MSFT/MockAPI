/**
 * @typedef {Object} MockEndpoint
 * @property {string} id Stable endpoint identifier.
 * @property {string} name Display name.
 * @property {string|null} [description] Optional operator-facing description.
 * @property {boolean} enabled Whether the endpoint participates in matching.
 * @property {readonly string[]} methods Configured HTTP methods.
 * @property {string} path Exact request path.
 * @property {number} [requestCount] Number of requests preselected in the endpoint test blade.
 * @property {MockResponse} response Configured terminal response behavior.
 */

/**
 * @typedef {Object} MockResponse
 * @property {"response"|"abortConnection"} [behavior] Omitted for the default HTTP response behavior.
 * @property {number|null} [statusCode] Terminal status; absent or null for an abort.
 * @property {string|null} [reasonPhrase] Optional HTTP/1.x reason phrase.
 * @property {Readonly<Record<string, readonly string[]>>} headers Ordered response header values.
 * @property {string|null} [contentType] Optional response media type.
 * @property {string} body Raw response body.
 * @property {MockRateLimit|null} [rateLimit] Optional rolling-window behavior.
 */

/**
 * @typedef {Object} MockRateLimit
 * @property {number} requestLimit Number of success responses allowed per window.
 * @property {number} windowSeconds Rolling-window duration.
 * @property {MockSuccessResponse} successResponse Response used before the limit is exceeded.
 */

/**
 * @typedef {object} MockSuccessResponse
 * @property {number} statusCode Permitted-request status (200 through 299).
 * @property {string|null} [reasonPhrase] Optional HTTP/1.x reason phrase.
 * @property {Readonly<Record<string, readonly string[]>>} headers Ordered response header values.
 * @property {string|null} [contentType] Optional response media type.
 * @property {string} body Raw response body.
 */

/**
 * @typedef {object} ConfigurationDocument
 * @property {string|null} [$schema] Optional JSON Schema reference.
 * @property {string} schemaVersion Configuration contract version, independent of application version.
 * @property {Readonly<Record<string, string>>|null} [apiDescriptions] Optional metadata keyed by case-sensitive first-segment paths, independent of endpoints.
 * @property {readonly MockEndpoint[]} endpoints Complete candidate configuration.
 */

/**
 * @typedef {object} ConfigurationStatus
 * @property {number} revision Active process-local revision.
 * @property {string} etag Quoted strong revision tag.
 * @property {boolean} hasUnsavedChanges Whether the active revision has unpersisted changes.
 * @property {Readonly<Record<string, string>>|null} [apiDescriptions] Descriptions keyed by case-sensitive first-segment paths.
 */

/**
 * @typedef {object} ApiSecurityStatus
 * @property {boolean} enabled Whether mock requests require an instance key.
 * @property {boolean} configured Whether a key hash has been persisted.
 * @property {string} etag Independent strong security-settings revision.
 */

/**
 * @typedef {object} ApiKeyCreated
 * @property {string} key Newly generated secret, returned only by rotation.
 * @property {ApiSecurityStatus} status Committed security settings.
 */

/**
 * @typedef {{path: string, code: string, message: string}} ConfigurationValidationError
 */

/**
 * @typedef {{isValid: boolean, errors: readonly ConfigurationValidationError[]}} ConfigurationValidationResult
 */

/**
 * @typedef {object} BuiltInMergeConflict
 * @property {string} builtInEndpointId Stable built-in endpoint ID.
 * @property {string} builtInName Built-in endpoint display name.
 * @property {"different"|"routeCollision"} kind Conflict category.
 * @property {string} existingEndpointId Stable conflicting endpoint ID.
 * @property {string} existingName Conflicting endpoint display name.
 */

/**
 * @typedef {ConfigurationStatus & {applied: boolean, forced: boolean, added: number,
 * updated: number, skipped: number, conflicts: readonly BuiltInMergeConflict[]}} BuiltInMergeResult
 */

/**
 * @typedef {object} ManagementProblem
 * @property {string} [type] Problem type URI, when provided.
 * @property {string} [title] Problem category.
 * @property {number} [status] HTTP status.
 * @property {string} [detail] Operator-facing explanation.
 * @property {string} [instance] Request path.
 * @property {readonly ConfigurationValidationError[]|null} [errors] Candidate validation failures.
 * @property {readonly BuiltInMergeConflict[]} [conflicts] Built-in merge conflict extension.
 */

/** @typedef {Error & {status?: number, problem?: ManagementProblem}} ManagementError */

/**
 * @typedef {object} MinuteStatistics
 * @property {string} minuteUtc Inclusive UTC start of the bucket.
 * @property {number} requests Logical requests in this minute.
 * @property {number} informationalResponses Responses with 1xx status.
 * @property {number} successResponses Responses with 2xx status.
 * @property {number} redirectionResponses Responses with 3xx status.
 * @property {number} clientErrorResponses Responses with 4xx status, including unmatched requests.
 * @property {number} serverErrorResponses Responses with 5xx status.
 */

/**
 * @typedef {object} RequestSummary
 * @property {string} timestampUtc UTC completion timestamp.
 * @property {string} method Normalized HTTP method.
 * @property {string} path Normalized path without query values.
 * @property {string|null} endpointId Matched stable ID, or null when unmatched.
 * @property {number|null} statusCode Response status, or null when aborted.
 * @property {"response"|"unmatched"|"failedWrite"|"aborted"} outcome Dispatch outcome.
 * @property {number} responseBytes Completed response-body byte count.
 * @property {number} transportAttempts Physical dispatches coalesced into this logical request.
 */

/**
 * @typedef {Object} EndpointStatistics
 * @property {string} endpointId Stable endpoint identifier.
 * @property {number} totalRequests Number of matched logical requests, including aborts and failed writes.
 * @property {number|null} lastStatusCode Most recently observed status, or null when aborted.
 * @property {string|null} lastRequestUtc Most recent UTC request time, or null before activity.
 * @property {number} responseBytes Completed response-body byte count.
 * @property {readonly MinuteStatistics[]} recentMinutes Sixty oldest-first UTC minute buckets.
 */

/**
 * @typedef {object} DashboardStatistics
 * @property {number} totalRequests All recorded logical requests.
 * @property {number} matchedRequests Requests matched to enabled endpoints.
 * @property {number} unmatchedRequests Requests without a matching enabled endpoint.
 * @property {number} failedWrites Matched responses that failed while writing.
 * @property {number} abortedConnections Requests intentionally aborted by configuration.
 * @property {number} informationalResponses Matched 1xx responses.
 * @property {number} successResponses Matched 2xx responses.
 * @property {number} redirectionResponses Matched 3xx responses.
 * @property {number} clientErrorResponses Matched 4xx responses.
 * @property {number} serverErrorResponses Matched 5xx responses.
 * @property {number} responseBytes Completed response-body byte count.
 * @property {readonly MinuteStatistics[]} recentMinutes Sixty oldest-first UTC minute buckets.
 * @property {readonly EndpointStatistics[]} endpoints Statistics indexed by stable endpoint ID.
 * @property {readonly RequestSummary[]} recentRequests At most 100 newest-first privacy-safe summaries.
 */

/** @typedef {{query: string, method: string, enabled: string, statusClass: string}} EndpointFilters */
/** @typedef {"name"|"methods"|"path"|"response"|"requests"|"lastRequest"|"enabled"} EndpointSortKey */
/** @typedef {{key: EndpointSortKey, direction: "ascending"|"descending"}} EndpointSort */
/** @typedef {{key: string, label: string, endpoints: MockEndpoint[]}} EndpointGroup */
/** @typedef {{kind: "notJson"|"empty"|"valid"|"invalid", message: string}} JsonBodyCheckResult */
/** @typedef {(text: string, successMessage: string) => Promise<void>} CopyToClipboard */

/**
 * Filters endpoints without mutating the input collection.
 *
 * @param {readonly MockEndpoint[]} endpoints Active endpoint definitions.
 * @param {EndpointFilters} filters Dashboard filter values.
 * @returns {MockEndpoint[]} Endpoints that satisfy every active filter.
 */
export function filterEndpoints(endpoints, filters) {
  const query = filters.query.trim().toLocaleLowerCase();
  return endpoints.filter(
    (endpoint) =>
      (!query ||
        endpoint.name.toLocaleLowerCase().includes(query) ||
        endpoint.description?.toLocaleLowerCase().includes(query) ||
        endpoint.path.toLocaleLowerCase().includes(query)) &&
      (!filters.method || endpoint.methods.includes(filters.method)) &&
      (!filters.enabled || String(endpoint.enabled) === filters.enabled) &&
      (!filters.statusClass ||
        (endpoint.response.behavior !== "abortConnection" &&
          String(Math.floor(endpoint.response.statusCode / 100)) === filters.statusClass))
  );
}

/**
 * Performs a stable, locale-aware endpoint sort.
 *
 * @param {readonly MockEndpoint[]} endpoints Endpoints to sort.
 * @param {EndpointSort} sort Sort selection.
 * @param {readonly EndpointStatistics[]} [statistics=[]] Statistics used by activity-related sort keys.
 * @returns {MockEndpoint[]} A sorted copy of the endpoint collection.
 */
export function sortEndpoints(endpoints, sort, statistics = []) {
  const statisticsByEndpoint = new Map(statistics.map((item) => [item.endpointId, item]));
  const direction = sort.direction === "descending" ? -1 : 1;
  const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });

  return endpoints
    .map((endpoint, index) => ({ endpoint, index }))
    .sort((left, right) => {
      const leftValue = endpointSortValue(left.endpoint, sort.key, statisticsByEndpoint);
      const rightValue = endpointSortValue(right.endpoint, sort.key, statisticsByEndpoint);
      const comparison =
        typeof leftValue === "number" && typeof rightValue === "number"
          ? leftValue - rightValue
          : collator.compare(String(leftValue), String(rightValue));
      return comparison === 0 ? left.index - right.index : comparison * direction;
    })
    .map(({ endpoint }) => endpoint);
}

function endpointSortValue(endpoint, key, statisticsByEndpoint) {
  const endpointStatistics = statisticsByEndpoint.get(endpoint.id);
  switch (key) {
    case "methods":
      return endpoint.methods.join(",");
    case "path":
      return endpoint.path;
    case "response":
      return endpoint.response.behavior === "abortConnection" ? 600 : endpoint.response.statusCode;
    case "requests":
      return endpointStatistics?.totalRequests || 0;
    case "lastRequest":
      return endpointStatistics?.lastRequestUtc ? Date.parse(endpointStatistics.lastRequestUtc) : 0;
    case "enabled":
      return endpoint.enabled ? 1 : 0;
    default:
      return endpoint.name;
  }
}

/**
 * Selects a bounded page and reports its one-based display range.
 *
 * @template T
 * @param {readonly T[]} items Complete item collection.
 * @param {number} requestedPage Requested one-based page number.
 * @param {number} pageSize Maximum items per page.
 * @returns {{items: T[], page: number, pageCount: number, start: number, end: number}} Page data and display bounds.
 */
export function paginateItems(items, requestedPage, pageSize) {
  const pageCount = Math.max(1, Math.ceil(items.length / pageSize));
  const page = Math.min(Math.max(1, requestedPage), pageCount);
  const startIndex = (page - 1) * pageSize;
  return {
    items: items.slice(startIndex, startIndex + pageSize),
    page,
    pageCount,
    start: items.length === 0 ? 0 : startIndex + 1,
    end: Math.min(startIndex + pageSize, items.length),
  };
}

/**
 * Reports whether retained statistics include activity before the initially visible window.
 *
 * @param {ReadonlyArray<Pick<MinuteStatistics, "requests">>} recentMinutes Retained minute buckets ordered from oldest to newest.
 * @param {number} visibleMinutes Number of newest buckets visible without horizontal scrolling.
 * @returns {boolean} Whether older activity makes horizontal scrolling useful.
 */
export function hasScrollableStatisticsHistory(recentMinutes, visibleMinutes) {
  const olderBucketCount = Math.max(0, recentMinutes.length - visibleMinutes);
  return recentMinutes.slice(0, olderBucketCount).some((bucket) => bucket.requests > 0);
}

/**
 * Groups endpoints by the first non-empty path segment while preserving input order.
 *
 * @param {readonly MockEndpoint[]} endpoints Endpoints to group.
 * @returns {EndpointGroup[]} Path groups.
 */
export function groupEndpointsByPath(endpoints) {
  const groups = new Map();
  for (const endpoint of endpoints) {
    const segment = endpoint.path.split("/").find(Boolean);
    const key = segment || "/";
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(endpoint);
  }
  return [...groups].map(([key, groupedEndpoints]) => ({
    key,
    label: key === "/" ? "/" : `${key}/`,
    endpoints: groupedEndpoints,
  }));
}

/**
 * Formats a byte count using binary units.
 *
 * @param {number} value Byte count.
 * @returns {string} Human-readable byte count.
 */
export function formatBytes(value) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KiB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MiB`;
}

/**
 * Formats a dashboard count using the browser's active locale.
 *
 * @param {number} value Numeric value to display.
 * @returns {string} Locale-aware number text.
 */
export function formatNumber(value) {
  return new Intl.NumberFormat().format(value);
}

/**
 * Formats an optional timestamp using the browser's active locale.
 *
 * @param {string|null|undefined} value Timestamp to display.
 * @returns {string} Locale-aware timestamp or the empty-state label.
 */
export function formatTime(value) {
  if (!value) return "Never";
  return new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "short" }).format(new Date(value));
}

/**
 * Formats an optional timestamp in UTC using the browser's active locale.
 *
 * @param {string|null|undefined} value Timestamp to display.
 * @returns {string} Locale-aware UTC timestamp or the empty-state label.
 */
export function formatUtcTime(value) {
  if (!value) return "Never";
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "short",
    timeStyle: "short",
    timeZone: "UTC",
  }).format(new Date(value));
}

/**
 * Groups newest-first request summaries into UTC minute buckets without changing their order.
 *
 * @template {{timestampUtc: string}} T
 * @param {readonly T[]} requests Recent request summaries.
 * @returns {{minuteUtc: string, requests: T[]}[]} Contiguous minute buckets preserving the input entry type.
 */
export function groupRequestsByMinute(requests) {
  const groups = [];

  for (const request of requests) {
    const minuteUtc = new Date(Math.floor(new Date(request.timestampUtc).getTime() / 60_000) * 60_000).toISOString();
    const currentGroup = groups.at(-1);
    if (currentGroup?.minuteUtc === minuteUtc) {
      currentGroup.requests.push(request);
      continue;
    }

    groups.push({ minuteUtc, requests: [request] });
  }

  return groups;
}

/**
 * Composes the plain-text representation copied from the test response panel.
 *
 * @param {{status: string, elapsed: string, url: string, headers: string, body: string}} response Visible response values.
 * @returns {string} Multi-section response details.
 */
export function formatTestResponseDetails(response) {
  return [
    `Status: ${response.status}`,
    `Elapsed: ${response.elapsed}`,
    `URL: ${response.url}`,
    "",
    response.headers,
    "",
    response.body,
  ].join("\n");
}

/**
 * Formats one request-log outcome for visible dashboard rendering.
 *
 * @param {Pick<RequestSummary, "outcome"> & Partial<Pick<RequestSummary, "statusCode">>} request Request outcome.
 * @returns {{label: string, className: string}} Visible label and semantic style class.
 */
export function formatRequestLogResult(request) {
  if (request.outcome === "aborted") return { label: "Aborted", className: "error" };
  if (request.outcome === "failedWrite") return { label: "Write failed", className: "error" };
  const statusCode = request.outcome === "unmatched" ? (request.statusCode ?? 404) : request.statusCode;
  const label = request.outcome === "unmatched" ? `${statusCode} · Unmatched` : String(statusCode ?? "Response");
  const className = statusCode ? `status-${Math.floor(statusCode / 100)}xx` : "response";
  return { label, className };
}

/**
 * Explains why grouped logical requests can have more than one transport attempt.
 *
 * @param {ReadonlyArray<{transportAttempts?: number}>} requests Recent logical request summaries.
 * @returns {string} Contextual explanation, or an empty string when no retries were observed.
 */
export function formatTransportAttemptInsight(requests) {
  const retriedRequests = requests.filter((request) => (request.transportAttempts || 1) > 1);
  if (retriedRequests.length === 0) return "";

  const transportAttempts = retriedRequests.reduce((total, request) => total + request.transportAttempts, 0);
  const testLabel = retriedRequests.length === 1 ? "dashboard test" : "dashboard tests";
  return `${retriedRequests.length} ${testLabel} produced ${transportAttempts} transport attempts. Chrome may automatically retry an idempotent request, such as GET, when the server intentionally closes the connection before sending HTTP response headers. MockAPI counts every transport attempt in statistics and groups attempts from the same dashboard test in this log.`;
}

/**
 * Pretty-prints JSON response text while preserving non-JSON and malformed payloads verbatim.
 *
 * @param {string} contentType Response content type.
 * @param {string} body Raw response body.
 * @returns {string} Formatted or original response body.
 */
export function formatJsonBody(contentType, body) {
  const mediaType = contentType.split(";", 1)[0].trim().toLocaleLowerCase();
  if (!body || (mediaType !== "application/json" && !mediaType.endsWith("+json"))) return body;

  try {
    return JSON.stringify(JSON.parse(body), null, 2);
  } catch {
    return body;
  }
}

/**
 * Checks response text when its content type identifies JSON.
 *
 * @param {string} contentType Response content type.
 * @param {string} body Raw response body.
 * @returns {JsonBodyCheckResult} JSON check result.
 */
export function checkJsonBody(contentType, body) {
  const mediaType = contentType.split(";", 1)[0].trim().toLocaleLowerCase();
  if (mediaType !== "application/json" && !mediaType.endsWith("+json")) {
    return { kind: "notJson", message: "Set a JSON content type to check this response body." };
  }
  if (!body.trim()) return { kind: "empty", message: "The JSON response body is empty." };

  try {
    JSON.parse(body);
    return { kind: "valid", message: "Valid JSON. The response body has been formatted." };
  } catch (error) {
    return { kind: "invalid", message: `Invalid JSON: ${error.message}` };
  }
}

/**
 * Summarizes the result of a built-in configuration merge.
 *
 * @param {Pick<BuiltInMergeResult, "applied"|"added"|"updated"|"skipped">} result Merge result.
 * @returns {string} Operator-facing summary.
 */
export function formatMergeResult(result) {
  if (!result.applied) {
    return "No example changes were needed";
  }
  const parts = [];
  if (result.added) parts.push(`${result.added} added`);
  if (result.updated) parts.push(`${result.updated} updated`);
  if (result.skipped) parts.push(`${result.skipped} already present`);
  return `Examples: ${parts.join(", ")}`;
}

/**
 * Converts a management API failure into an actionable dashboard message.
 *
 * @param {Pick<ManagementError, "message"|"problem">} error API or local error.
 * @returns {string} Validation details when available, otherwise the base error message.
 */
export function formatProblem(error) {
  const validation = error.problem?.errors;
  if (Array.isArray(validation) && validation.length) {
    return validation.map((item) => `${item.path}: ${item.message}`).join("\n");
  }
  return error.message;
}

/**
 * Parses newline-delimited request headers in `Name: value` form.
 *
 * @param {string} value Header editor contents.
 * @returns {[string, string][]} Header name/value pairs in input order.
 * @throws {Error} When a non-empty line has no header-name separator.
 */
export function parseHeaderLines(value) {
  /** @type {[string, string][]} */
  const pairs = [];
  for (const [index, line] of value.split("\n").entries()) {
    if (!line.trim()) continue;
    const separator = line.indexOf(":");
    if (separator <= 0) throw new Error(`Request header line ${index + 1} must use Name: value.`);
    pairs.push([line.slice(0, separator).trim(), line.slice(separator + 1).trim()]);
  }
  return pairs;
}

/**
 * Reports whether a test request method may include a body.
 *
 * @param {string} method Uppercase HTTP method.
 * @returns {boolean} `false` for GET and HEAD; otherwise `true`.
 */
export function methodSupportsBody(method) {
  return method !== "GET" && method !== "HEAD";
}

/**
 * Explains how configuration and observed activity produced an endpoint's statistics.
 *
 * @param {MockEndpoint} endpoint Endpoint configuration.
 * @param {EndpointStatistics|undefined} statistics Process-local endpoint statistics.
 * @returns {string} Operator-facing explanation.
 */
export function explainEndpointStatistics(endpoint, statistics) {
  if (endpoint.response.behavior === "abortConnection") {
    return statistics?.totalRequests
      ? "Every matched request intentionally aborts the connection before an HTTP response is sent."
      : "No attempts have been observed. This endpoint is configured to abort the connection without an HTTP response.";
  }

  if (!statistics?.totalRequests) {
    if (endpoint.response.rateLimit) {
      const { requestLimit, windowSeconds, successResponse } = endpoint.response.rateLimit;
      return `No attempts have been observed. The first ${requestLimit} requests within ${windowSeconds} seconds return HTTP ${successResponse.statusCode}; later requests return HTTP ${endpoint.response.statusCode}.`;
    }
    return `No attempts have been observed. This endpoint is configured to return HTTP ${endpoint.response.statusCode} for every matched request.`;
  }

  const configuredStatus = endpoint.response.statusCode;
  if (endpoint.response.rateLimit) {
    const { requestLimit, windowSeconds, successResponse } = endpoint.response.rateLimit;
    return `The first ${requestLimit} requests within ${windowSeconds} seconds return HTTP ${successResponse.statusCode}; later requests return HTTP ${configuredStatus}. The last observed response was HTTP ${statistics.lastStatusCode}.`;
  }
  if (statistics.lastStatusCode !== configuredStatus) {
    return `The last observed response was HTTP ${statistics.lastStatusCode}, but the endpoint is now configured for HTTP ${configuredStatus}. These statistics include activity from before the latest configuration change.`;
  }

  if (configuredStatus === 429) {
    const retryAfter = Object.entries(endpoint.response.headers || {}).find(
      ([name]) => name.toLocaleLowerCase() === "retry-after"
    )?.[1]?.[0];
    const retryExplanation = retryAfter ? ` The configured Retry-After value is ${retryAfter}.` : "";
    return `Every matched request is configured to return HTTP 429.${retryExplanation} No request-count or time-window rate-limit condition is configured.`;
  }

  return `Every matched request is configured to return HTTP ${configuredStatus}. The activity graph shows when attempts reached this endpoint, not a conditional response rule.`;
}
