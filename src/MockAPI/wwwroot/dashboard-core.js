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
