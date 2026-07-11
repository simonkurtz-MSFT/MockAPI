"use strict";

const API = "/__mockapi/api";
const METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
const state = {
  endpoints: [],
  statistics: null,
  etag: null,
  revision: 0,
  dirty: false,
  editingId: null,
  pendingConfirm: null,
  mergeInFlight: false,
  testAbort: null,
  testTrigger: null,
  testEndpointId: null
};

const elements = Object.fromEntries([
  "connection-status", "persistence-label", "save-button", "create-button", "import-button",
  "load-template-button", "load-example-button", "empty-load-template-button", "empty-load-example-button",
  "import-file", "reset-statistics",
  "metric-total", "metric-matched", "metric-unmatched",
  "metric-bytes", "metric-rate", "rate-bars", "endpoint-count", "endpoint-rows", "empty-state",
  "empty-state-title", "empty-state-description", "starter-actions",
  "filter-text", "filter-method", "filter-enabled", "filter-status", "endpoint-dialog", "endpoint-form", "dialog-title",
  "form-error", "field-name", "field-path", "field-status", "field-reason", "field-content-type",
  "field-body", "field-enabled", "method-options", "header-rows", "add-header", "copy-url",
  "confirm-dialog", "confirm-title", "confirm-message", "confirm-cancel", "confirm-accept",
  "test-blade-shell", "test-blade-backdrop", "test-blade", "test-blade-title", "test-blade-context",
  "test-blade-close", "test-method", "test-path", "test-request-headers", "test-request-body",
  "test-send", "test-cancel", "test-copy-url", "test-copy-response", "test-status",
  "test-response-status", "test-response-time", "test-response-url", "test-response-headers", "test-response-body",
  "toast-region", "theme-toggle"
].map(id => [id, document.getElementById(id)]));

function makeElement(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function showToast(message, isError = false) {
  const toast = makeElement("div", `toast${isError ? " error" : ""}`, message);
  elements["toast-region"].append(toast);
  window.setTimeout(() => toast.remove(), 4200);
}

function setConnection(online) {
  const element = elements["connection-status"];
  element.className = `connection-status ${online ? "online" : "offline"}`;
  element.lastChild.textContent = online ? "Live" : "Disconnected";
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body !== undefined) headers.set("Content-Type", "application/json");
  if (options.mutatesConfiguration && state.etag) headers.set("If-Match", state.etag);
  const response = await fetch(`${API}${path}`, { ...options, headers });
  const responseEtag = response.headers.get("ETag");
  if (responseEtag) state.etag = responseEtag;
  if (!response.ok) {
    let problem;
    try { problem = await response.json(); } catch { problem = {}; }
    const error = new Error(problem.detail || `${response.status} ${response.statusText}`);
    error.problem = problem;
    error.status = response.status;
    throw error;
  }
  if (response.status === 204) return null;
  return response.json();
}

async function refresh() {
  try {
    const [configuration, endpoints, statistics] = await Promise.all([
      api("/configuration"), api("/endpoints"), api("/statistics")
    ]);
    state.revision = configuration.revision;
    state.etag = configuration.etag;
    state.dirty = configuration.hasUnsavedChanges;
    state.endpoints = endpoints;
    state.statistics = statistics;
    setConnection(true);
    render();
  } catch (error) {
    setConnection(false);
    elements["persistence-label"].textContent = "Management API unavailable";
    console.error(error);
  }
}

function render() {
  elements["persistence-label"].textContent = state.dirty
    ? `Revision ${state.revision} · Unsaved changes`
    : `Revision ${state.revision} · Persisted`;
  elements["save-button"].disabled = !state.dirty;
  renderStatistics();
  renderEndpoints();
}

function renderStatistics() {
  const statistics = state.statistics;
  if (!statistics) return;
  elements["metric-total"].textContent = formatNumber(statistics.totalRequests);
  elements["metric-matched"].textContent = formatNumber(statistics.matchedRequests);
  elements["metric-unmatched"].textContent = formatNumber(statistics.unmatchedRequests);
  elements["metric-bytes"].textContent = formatBytes(statistics.responseBytes);
  const recent = statistics.recentMinutes || [];
  const recentTotal = recent.reduce((sum, bucket) => sum + bucket.requests, 0);
  elements["metric-rate"].textContent = `${(recentTotal / 60).toFixed(1)} req/min`;
  elements["rate-bars"].replaceChildren();
  const display = recent.slice(-30);
  const maximum = Math.max(1, ...display.map(bucket => bucket.requests));
  for (const bucket of display) {
    const bar = makeElement("i");
    bar.style.height = `${Math.max(5, (bucket.requests / maximum) * 100)}%`;
    elements["rate-bars"].append(bar);
  }
}

function endpointStatistics(id) {
  return state.statistics?.endpoints?.find(item => item.endpointId === id);
}

function renderEndpoints() {
  const query = elements["filter-text"].value.trim().toLocaleLowerCase();
  const method = elements["filter-method"].value;
  const enabled = elements["filter-enabled"].value;
  const statusClass = elements["filter-status"].value;
  const visible = state.endpoints.filter(endpoint =>
    (!query || endpoint.name.toLocaleLowerCase().includes(query) || endpoint.path.toLocaleLowerCase().includes(query)) &&
    (!method || endpoint.methods.includes(method)) &&
    (!enabled || String(endpoint.enabled) === enabled) &&
    (!statusClass || String(Math.floor(endpoint.response.statusCode / 100)) === statusClass));

  elements["endpoint-count"].textContent = visible.length;
  elements["endpoint-rows"].replaceChildren();
  elements["empty-state"].hidden = visible.length !== 0;
  const hasEndpoints = state.endpoints.length !== 0;
  elements["empty-state-title"].textContent = hasEndpoints ? "No endpoints match" : "No endpoints yet";
  elements["empty-state-description"].textContent = hasEndpoints
    ? "Adjust the active filters to see configured endpoints."
    : "Load working examples or begin with a blank configuration.";
  elements["starter-actions"].hidden = hasEndpoints;
  for (const endpoint of visible) elements["endpoint-rows"].append(createEndpointRow(endpoint));
}

function createEndpointRow(endpoint) {
  const row = document.createElement("tr");
  const statistics = endpointStatistics(endpoint.id);

  const nameCell = document.createElement("td");
  const name = makeElement("div", "endpoint-name");
  name.append(makeElement("strong", null, endpoint.name), makeElement("small", null, endpoint.id.slice(0, 8)));
  nameCell.append(name);

  const methodCell = document.createElement("td");
  const methods = makeElement("div", "method-list");
  for (const item of endpoint.methods) methods.append(makeElement("span", "method-badge", item));
  methodCell.append(methods);

  const pathCell = makeElement("td", "path-cell", endpoint.path);
  const statusCell = document.createElement("td");
  statusCell.append(makeElement("span", "status-badge", String(endpoint.response.statusCode)));
  const requestsCell = makeElement("td", "numeric", formatNumber(statistics?.totalRequests || 0));
  const lastCell = makeElement("td", null, formatTime(statistics?.lastRequestUtc));

  const enabledCell = document.createElement("td");
  const switchLabel = makeElement("label", "switch");
  const toggle = document.createElement("input");
  toggle.type = "checkbox";
  toggle.checked = endpoint.enabled;
  toggle.setAttribute("aria-label", `${endpoint.enabled ? "Disable" : "Enable"} ${endpoint.name}`);
  toggle.addEventListener("change", () => toggleEndpoint(endpoint, toggle.checked));
  switchLabel.append(toggle, makeElement("span"));
  enabledCell.append(switchLabel);

  const actionCell = document.createElement("td");
  const actions = makeElement("div", "row-actions");
  const testButton = actionButton("Test", "▶", event => openTestBlade(endpoint, event.currentTarget));
  testButton.dataset.testEndpointId = endpoint.id;
  actions.append(
    testButton,
    actionButton("Edit", "✎", () => openEndpointDialog(endpoint)),
    actionButton("Duplicate", "⧉", () => duplicateEndpoint(endpoint)),
    actionButton("Delete", "⌫", () => requestDelete(endpoint))
  );
  actionCell.append(actions);

  row.append(nameCell, methodCell, pathCell, statusCell, requestsCell, lastCell, enabledCell, actionCell);
  return row;
}

function actionButton(label, icon, handler) {
  const button = makeElement("button", "icon-button", icon);
  button.type = "button";
  button.title = label;
  button.setAttribute("aria-label", label);
  button.addEventListener("click", handler);
  return button;
}

function buildMethodOptions() {
  for (const method of METHODS) {
    const label = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.name = "method";
    checkbox.value = method;
    label.append(checkbox, makeElement("span", null, method));
    elements["method-options"].append(label);
  }
}

function addHeaderRow(name = "", values = []) {
  const row = makeElement("div", "header-row");
  const nameInput = document.createElement("input");
  nameInput.placeholder = "Header name";
  nameInput.setAttribute("aria-label", "Header name");
  nameInput.value = name;
  const valueInput = document.createElement("input");
  valueInput.placeholder = "One value per line";
  valueInput.setAttribute("aria-label", "Header values, one per line");
  valueInput.value = values.join("\n");
  const remove = actionButton("Remove header", "×", () => row.remove());
  row.append(nameInput, valueInput, remove);
  elements["header-rows"].append(row);
}

function openEndpointDialog(endpoint = null) {
  state.editingId = endpoint?.id || null;
  elements["dialog-title"].textContent = endpoint ? "Edit endpoint" : "New endpoint";
  elements["form-error"].hidden = true;
  elements["field-name"].value = endpoint?.name || "";
  elements["field-path"].value = endpoint?.path || "/api/";
  elements["field-status"].value = endpoint?.response.statusCode || 200;
  elements["field-reason"].value = endpoint?.response.reasonPhrase || "";
  elements["field-content-type"].value = endpoint?.response.contentType || "application/json; charset=utf-8";
  elements["field-body"].value = endpoint?.response.body || "";
  elements["field-enabled"].checked = endpoint?.enabled ?? true;
  for (const input of elements["method-options"].querySelectorAll("input")) {
    input.checked = endpoint ? endpoint.methods.includes(input.value) : input.value === "GET";
  }
  elements["header-rows"].replaceChildren();
  const headers = endpoint?.response.headers || {};
  for (const [name, values] of Object.entries(headers)) addHeaderRow(name, values);
  elements["endpoint-dialog"].showModal();
  elements["field-name"].focus();
}

function readEndpointForm() {
  const methods = [...elements["method-options"].querySelectorAll("input:checked")].map(input => input.value);
  const headers = {};
  for (const row of elements["header-rows"].children) {
    const inputs = row.querySelectorAll("input");
    const name = inputs[0].value.trim();
    if (!name) continue;
    headers[name] = inputs[1].value.split("\n").map(value => value.trim()).filter(Boolean);
  }
  return {
    id: state.editingId || crypto.randomUUID(),
    name: elements["field-name"].value.trim(),
    enabled: elements["field-enabled"].checked,
    methods,
    path: elements["field-path"].value.trim(),
    response: {
      statusCode: Number(elements["field-status"].value),
      reasonPhrase: elements["field-reason"].value.trim() || null,
      headers,
      contentType: elements["field-content-type"].value.trim() || null,
      body: elements["field-body"].value
    }
  };
}

async function submitEndpoint(event) {
  event.preventDefault();
  const endpoint = readEndpointForm();
  if (endpoint.methods.length === 0) {
    showFormError("Select at least one HTTP method.");
    return;
  }
  try {
    const editing = Boolean(state.editingId);
    await api(editing ? `/endpoints/${endpoint.id}` : "/endpoints", {
      method: editing ? "PUT" : "POST",
      body: JSON.stringify(endpoint),
      mutatesConfiguration: true
    });
    elements["endpoint-dialog"].close();
    showToast(editing ? "Endpoint updated" : "Endpoint created");
    await refresh();
  } catch (error) {
    showFormError(formatProblem(error));
  }
}

function showFormError(message) {
  elements["form-error"].textContent = message;
  elements["form-error"].hidden = false;
  elements["form-error"].focus();
}

function formatProblem(error) {
  const validation = error.problem?.errors;
  if (Array.isArray(validation) && validation.length) {
    return validation.map(item => `${item.path}: ${item.message}`).join("\n");
  }
  return error.message;
}

async function toggleEndpoint(endpoint, enabled) {
  try {
    await api(`/endpoints/${endpoint.id}/enabled`, {
      method: "PUT", body: JSON.stringify({ enabled }), mutatesConfiguration: true
    });
    await refresh();
  } catch (error) {
    showToast(formatProblem(error), true);
    await refresh();
  }
}

function duplicateEndpoint(endpoint) {
  const copy = structuredClone(endpoint);
  copy.id = crypto.randomUUID();
  copy.name = `${copy.name} copy`;
  copy.path = `${copy.path}-copy`;
  state.editingId = null;
  openEndpointDialog(copy);
  state.editingId = null;
  elements["dialog-title"].textContent = "Duplicate endpoint";
}

function requestDelete(endpoint) {
  confirmAction("Delete endpoint", `Delete “${endpoint.name}”? This changes the active configuration immediately.`, async () => {
    await api(`/endpoints/${endpoint.id}`, { method: "DELETE", mutatesConfiguration: true });
    showToast("Endpoint deleted");
    await refresh();
  });
}

function confirmAction(title, message, action, acceptLabel = "Confirm") {
  elements["confirm-title"].textContent = title;
  elements["confirm-message"].textContent = message;
  state.pendingConfirm = action;
  elements["confirm-accept"].textContent = acceptLabel;
  elements["confirm-dialog"].showModal();
  elements["confirm-accept"].focus();
}

async function acceptConfirmation() {
  const action = state.pendingConfirm;
  state.pendingConfirm = null;
  elements["confirm-dialog"].close();
  if (!action) return;
  try { await action(); } catch (error) { showToast(formatProblem(error), true); await refresh(); }
}

async function importConfiguration(file) {
  let document;
  try { document = JSON.parse(await file.text()); }
  catch { showToast("The selected file does not contain valid JSON.", true); return; }
  await loadConfiguration(document, "Import configuration", "Configuration imported");
}

async function loadBuiltInConfiguration(name) {
  if (state.mergeInFlight) {
    showToast("A built-in configuration merge is already in progress.", true);
    return;
  }
  state.mergeInFlight = true;
  try {
    const result = await api(`/configuration/${name}/merge`, {
      method: "POST", mutatesConfiguration: true
    });
    showToast(formatMergeResult(name, result));
    await refresh();
  } catch (error) {
    if (error.status !== 409 || !Array.isArray(error.problem?.conflicts)) {
      showToast(formatProblem(error), true);
      return;
    }
    const details = error.problem.conflicts
      .map(conflict => `${conflict.builtInName} conflicts with ${conflict.existingName}`)
      .join("; ");
    confirmAction(
      "Built-in configuration conflicts",
      `${details}. No changes were made. Force update applies the built-in versions and preserves unrelated endpoints.`,
      async () => {
        const result = await api(`/configuration/${name}/merge?force=true`, {
          method: "POST", mutatesConfiguration: true
        });
        showToast(formatMergeResult(name, result));
        await refresh();
      },
      "Force update");
  } finally { state.mergeInFlight = false; }
}

function formatMergeResult(name, result) {
  if (!result.applied) return `No ${name} changes were needed`;
  const parts = [];
  if (result.added) parts.push(`${result.added} added`);
  if (result.updated) parts.push(`${result.updated} updated`);
  if (result.skipped) parts.push(`${result.skipped} already present`);
  return `${name === "example" ? "Examples" : "Template"}: ${parts.join(", ")}`;
}

async function loadConfiguration(document, title, successMessage) {
  try {
    const validation = await api("/configuration/validate", { method: "POST", body: JSON.stringify(document) });
    if (!validation.isValid) {
      showToast(validation.errors.map(error => `${error.path}: ${error.message}`).join(" · "), true);
      return;
    }
    confirmAction(title, `Replace the active configuration with ${document.endpoints.length} endpoint(s)?`, async () => {
      await api("/configuration/import", { method: "PUT", body: JSON.stringify(document), mutatesConfiguration: true });
      showToast(successMessage);
      await refresh();
    });
  } catch (error) { showToast(formatProblem(error), true); }
}

async function saveConfiguration() {
  try {
    await api("/configuration/save", { method: "POST", mutatesConfiguration: true });
    showToast("Configuration saved");
    await refresh();
  } catch (error) { showToast(formatProblem(error), true); }
}

function openTestBlade(endpoint, trigger) {
  state.testTrigger = trigger;
  state.testEndpointId = endpoint.id;
  elements["test-blade-title"].textContent = endpoint.name;
  elements["test-blade-context"].textContent = endpoint.path;
  elements["test-method"].replaceChildren(...endpoint.methods.map(method => {
    const option = document.createElement("option");
    option.value = method;
    option.textContent = method;
    return option;
  }));
  elements["test-path"].value = endpoint.path;
  elements["test-request-headers"].value = "";
  elements["test-request-body"].value = "";
  resetTestResponse();
  elements["test-blade-shell"].hidden = false;
  document.body.classList.add("blade-open");
  elements["test-method"].focus();
}

function closeTestBlade() {
  state.testAbort?.abort();
  state.testAbort = null;
  elements["test-blade-shell"].hidden = true;
  document.body.classList.remove("blade-open");
  const currentTrigger = state.testEndpointId
    ? document.querySelector(`[data-test-endpoint-id="${CSS.escape(state.testEndpointId)}"]`)
    : null;
  (state.testTrigger?.isConnected ? state.testTrigger : currentTrigger)?.focus();
  state.testTrigger = null;
  state.testEndpointId = null;
}

function resetTestResponse() {
  elements["test-status"].textContent = "Send a request to inspect its response.";
  elements["test-response-status"].textContent = "—";
  elements["test-response-time"].textContent = "—";
  elements["test-response-url"].textContent = "—";
  elements["test-response-headers"].textContent = "No response yet.";
  elements["test-response-body"].textContent = "No response yet.";
  elements["test-copy-response"].disabled = true;
}

function parseTestHeaders(value) {
  const headers = new Headers();
  for (const [index, line] of value.split("\n").entries()) {
    if (!line.trim()) continue;
    const separator = line.indexOf(":");
    if (separator <= 0) throw new Error(`Request header line ${index + 1} must use Name: value.`);
    headers.append(line.slice(0, separator).trim(), line.slice(separator + 1).trim());
  }
  return headers;
}

async function sendTestRequest() {
  const method = elements["test-method"].value;
  const url = new URL(elements["test-path"].value, window.location.origin);
  if (url.origin !== window.location.origin) {
    showToast("Endpoint tests must target the current MockAPI origin.", true);
    return;
  }

  let headers;
  try { headers = parseTestHeaders(elements["test-request-headers"].value); }
  catch (error) { showToast(error.message, true); return; }

  const controller = new AbortController();
  state.testAbort = controller;
  elements["test-send"].disabled = true;
  elements["test-cancel"].disabled = false;
  elements["test-status"].textContent = "Sending request…";
  const started = performance.now();
  try {
    const options = { method, headers, signal: controller.signal };
    if (method !== "GET" && method !== "HEAD") options.body = elements["test-request-body"].value;
    const response = await fetch(url, options);
    const body = await response.text();
    const elapsed = performance.now() - started;
    const responseHeaders = [...response.headers.entries()].map(([name, value]) => `${name}: ${value}`).join("\n");
    elements["test-response-status"].textContent = `${response.status} ${response.statusText}`.trim();
    elements["test-response-time"].textContent = `${elapsed.toFixed(1)} ms`;
    elements["test-response-url"].textContent = response.url;
    elements["test-response-headers"].textContent = responseHeaders || "No response headers.";
    elements["test-response-body"].textContent = body || "Empty response body.";
    elements["test-status"].textContent = `Request completed with HTTP ${response.status}.`;
    elements["test-copy-response"].disabled = false;
  } catch (error) {
    elements["test-status"].textContent = error.name === "AbortError" ? "Request cancelled." : `Network error: ${error.message}`;
  } finally {
    state.testAbort = null;
    elements["test-send"].disabled = false;
    elements["test-cancel"].disabled = true;
  }
}

function testResponseText() {
  return [
    `Status: ${elements["test-response-status"].textContent}`,
    `Elapsed: ${elements["test-response-time"].textContent}`,
    `URL: ${elements["test-response-url"].textContent}`,
    "", elements["test-response-headers"].textContent,
    "", elements["test-response-body"].textContent
  ].join("\n");
}

function trapBladeFocus(event) {
  if (event.key !== "Tab") return;
  const focusable = [...elements["test-blade"].querySelectorAll("button:not(:disabled), input, select, textarea, [tabindex='0']")];
  if (focusable.length === 0) return;
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
}

function formatNumber(value) { return new Intl.NumberFormat().format(value); }
function formatBytes(value) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KiB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MiB`;
}
function formatTime(value) {
  if (!value) return "Never";
  return new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "short" }).format(new Date(value));
}

function bindEvents() {
  elements["create-button"].addEventListener("click", () => openEndpointDialog());
  elements["endpoint-form"].addEventListener("submit", submitEndpoint);
  elements["add-header"].addEventListener("click", () => addHeaderRow());
  elements["copy-url"].addEventListener("click", async () => {
    await navigator.clipboard.writeText(new URL(elements["field-path"].value, window.location.origin).href);
    showToast("Endpoint URL copied");
  });
  for (const button of document.querySelectorAll("[data-close]")) {
    button.addEventListener("click", () => elements["endpoint-dialog"].close());
  }
  for (const filter of [elements["filter-text"], elements["filter-method"], elements["filter-enabled"], elements["filter-status"]]) {
    filter.addEventListener("input", renderEndpoints);
  }
  elements["load-template-button"].addEventListener("click", () => loadBuiltInConfiguration("template"));
  elements["load-example-button"].addEventListener("click", () => loadBuiltInConfiguration("example"));
  elements["empty-load-template-button"].addEventListener("click", () => loadBuiltInConfiguration("template"));
  elements["empty-load-example-button"].addEventListener("click", () => loadBuiltInConfiguration("example"));
  elements["import-button"].addEventListener("click", () => elements["import-file"].click());
  elements["import-file"].addEventListener("change", () => {
    const file = elements["import-file"].files[0];
    if (file) importConfiguration(file);
    elements["import-file"].value = "";
  });
  elements["save-button"].addEventListener("click", saveConfiguration);
  elements["reset-statistics"].addEventListener("click", () => confirmAction(
    "Reset statistics", "Reset all process-local request statistics?", async () => {
      await api("/statistics/reset", { method: "POST" });
      showToast("Statistics reset");
      await refresh();
    }));
  elements["confirm-cancel"].addEventListener("click", () => elements["confirm-dialog"].close());
  elements["confirm-accept"].addEventListener("click", acceptConfirmation);
  elements["test-blade-close"].addEventListener("click", closeTestBlade);
  elements["test-blade-backdrop"].addEventListener("click", closeTestBlade);
  elements["test-blade"].addEventListener("keydown", trapBladeFocus);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && !elements["test-blade-shell"].hidden) {
      event.preventDefault();
      closeTestBlade();
    }
  });
  elements["test-send"].addEventListener("click", sendTestRequest);
  elements["test-cancel"].addEventListener("click", () => state.testAbort?.abort());
  elements["test-copy-url"].addEventListener("click", async () => {
    await navigator.clipboard.writeText(new URL(elements["test-path"].value, window.location.origin).href);
    showToast("Request URL copied");
  });
  elements["test-copy-response"].addEventListener("click", async () => {
    await navigator.clipboard.writeText(testResponseText());
    showToast("Response details copied");
  });
  elements["theme-toggle"].addEventListener("click", () => {
    const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
  });
}

function connectStatisticsStream() {
  if (!("EventSource" in window)) return;
  const events = new EventSource(`${API}/statistics/events`);
  events.addEventListener("statistics", event => {
    state.statistics = JSON.parse(event.data);
    setConnection(true);
    renderStatistics();
    renderEndpoints();
  });
  events.onerror = () => setConnection(false);
}

buildMethodOptions();
bindEvents();
refresh();
connectStatisticsStream();
window.setInterval(refresh, 3000);
