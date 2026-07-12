export function filterEndpoints(endpoints, filters) {
  const query = filters.query.trim().toLocaleLowerCase();
  return endpoints.filter(
    (endpoint) =>
      (!query ||
        endpoint.name.toLocaleLowerCase().includes(query) ||
        endpoint.path.toLocaleLowerCase().includes(query)) &&
      (!filters.method || endpoint.methods.includes(filters.method)) &&
      (!filters.enabled || String(endpoint.enabled) === filters.enabled) &&
      (!filters.statusClass || String(Math.floor(endpoint.response.statusCode / 100)) === filters.statusClass)
  );
}

export function formatBytes(value) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KiB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MiB`;
}

export function formatMergeResult(name, result) {
  if (!result.applied) return `No ${name} changes were needed`;
  const parts = [];
  if (result.added) parts.push(`${result.added} added`);
  if (result.updated) parts.push(`${result.updated} updated`);
  if (result.skipped) parts.push(`${result.skipped} already present`);
  return `${name === "example" ? "Examples" : "Template"}: ${parts.join(", ")}`;
}

export function formatProblem(error) {
  const validation = error.problem?.errors;
  if (Array.isArray(validation) && validation.length) {
    return validation.map((item) => `${item.path}: ${item.message}`).join("\n");
  }
  return error.message;
}

export function parseHeaderLines(value) {
  const pairs = [];
  for (const [index, line] of value.split("\n").entries()) {
    if (!line.trim()) continue;
    const separator = line.indexOf(":");
    if (separator <= 0) throw new Error(`Request header line ${index + 1} must use Name: value.`);
    pairs.push([line.slice(0, separator).trim(), line.slice(separator + 1).trim()]);
  }
  return pairs;
}

export function methodSupportsBody(method) {
  return method !== "GET" && method !== "HEAD";
}

export function explainEndpointStatistics(endpoint, statistics) {
  if (!statistics?.totalRequests) {
    return `No requests have been observed. This endpoint is configured to return HTTP ${endpoint.response.statusCode} for every matched request.`;
  }

  const configuredStatus = endpoint.response.statusCode;
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

  return `Every matched request is configured to return HTTP ${configuredStatus}. The activity graph shows when requests reached this endpoint, not a conditional response rule.`;
}
