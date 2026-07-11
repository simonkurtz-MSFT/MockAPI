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
  pendingConfirm: null
};

const elements = Object.fromEntries([
  "connection-status", "persistence-label", "save-button", "create-button", "import-button",
  "load-template-button", "load-example-button", "import-file", "reset-statistics",
  "metric-total", "metric-matched", "metric-unmatched",
  "metric-bytes", "metric-rate", "rate-bars", "endpoint-count", "endpoint-rows", "empty-state",
  "filter-text", "filter-method", "filter-enabled", "endpoint-dialog", "endpoint-form", "dialog-title",
  "form-error", "field-name", "field-path", "field-status", "field-reason", "field-content-type",
  "field-body", "field-enabled", "method-options", "header-rows", "add-header", "copy-url",
  "confirm-dialog", "confirm-title", "confirm-message", "confirm-cancel", "confirm-accept",
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
  const visible = state.endpoints.filter(endpoint =>
    (!query || endpoint.name.toLocaleLowerCase().includes(query) || endpoint.path.toLocaleLowerCase().includes(query)) &&
    (!method || endpoint.methods.includes(method)) &&
    (!enabled || String(endpoint.enabled) === enabled));

  elements["endpoint-count"].textContent = visible.length;
  elements["endpoint-rows"].replaceChildren();
  elements["empty-state"].hidden = visible.length !== 0;
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
  actions.append(
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

function confirmAction(title, message, action) {
  elements["confirm-title"].textContent = title;
  elements["confirm-message"].textContent = message;
  state.pendingConfirm = action;
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
  try {
    const response = await fetch(`${API}/configuration/${name}`, { cache: "no-store" });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    const document = await response.json();
    const label = name === "template" ? "template" : "example";
    await loadConfiguration(document, `Load ${label}`, `${label[0].toUpperCase()}${label.slice(1)} loaded`);
  } catch (error) { showToast(formatProblem(error), true); }
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
  for (const filter of [elements["filter-text"], elements["filter-method"], elements["filter-enabled"]]) {
    filter.addEventListener("input", renderEndpoints);
  }
  elements["load-template-button"].addEventListener("click", () => loadBuiltInConfiguration("template"));
  elements["load-example-button"].addEventListener("click", () => loadBuiltInConfiguration("example"));
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
