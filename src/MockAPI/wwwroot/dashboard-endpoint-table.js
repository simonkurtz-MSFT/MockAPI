"use strict";

import {
  filterEndpoints,
  formatNumber,
  formatTime,
  getEndpointDisplayPath,
  groupEndpointsByPath,
  sortEndpoints,
} from "./dashboard-core.js?v={{ASSET_VERSION}}";
import { HTTP_STATUS_REASONS } from "./dashboard-endpoint-editor.js?v={{ASSET_VERSION}}";
import {
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "./dashboard-dom.js?v={{ASSET_VERSION}}";

/**
 * @typedef {object} DashboardEndpointTable
 * @property {(endpoints: readonly import("./dashboard-core.js").MockEndpoint[]) => void} updateEndpoints Replaces the read-only snapshot and reconciles selection by stable ID.
 * @property {(descriptions: Readonly<Record<string, string>>|null|undefined) => void} updateApiDescriptions Replaces path-based metadata from the same configuration revision.
 * @property {(statistics: import("./dashboard-core.js").DashboardStatistics) => void} updateStatistics Refreshes activity cells without replacing unchanged row controls.
 * @property {(pending: boolean) => void} setPending Disables mutation controls and preserves toggle focus.
 * @property {() => void} clearSelection Clears selected IDs and renders.
 * @property {() => void} render Renders from the current inputs and view state.
 * @property {() => void} dispose Removes static/row listeners. The coordinator must stop supplying updates after disposal.
 */
/**
 * Owns endpoint rows, selection, filters, group-local sorting, collapse state, and control focus.
 * Configuration and statistics updates are explicit inputs; commands are delegated without sharing mutable state.
 * @param {object} options Dependencies.
 * @param {Document} options.documentRoot Dashboard document.
 * @param {import("./dashboard-preferences.js").DashboardPreferencesStore} options.preferencesStore View preferences.
 * @param {(endpoint: import("./dashboard-core.js").MockEndpoint) => void} options.onEdit Opens the editor.
 * @param {(endpoint: import("./dashboard-core.js").MockEndpoint) => void} options.onDuplicate Opens a duplicate draft.
 * @param {(endpoint: import("./dashboard-core.js").MockEndpoint) => void} options.onDelete Requests confirmation.
 * @param {(endpoint: import("./dashboard-core.js").MockEndpoint, trigger: HTMLElement) => void} options.onTest Opens the test blade.
 * @param {(endpoint: import("./dashboard-core.js").MockEndpoint, enabled: boolean) => Promise<void>} options.onToggle Updates enabled state.
 * @param {(operation: "enable"|"disable", ids: string[]) => Promise<void>} options.onBulkOperation Applies a selection snapshot.
 * @param {(ids: string[]) => void} options.onBulkDelete Confirms a selection snapshot.
 * @param {(path: string, description: string) => void} options.onEditApiDescription Opens the API description editor.
 * @returns {DashboardEndpointTable} Endpoint view owner; selection callbacks receive detached ID arrays.
 * @throws {Error} Required dashboard markup is missing or has an incorrect tag.
 */
export function createDashboardEndpointTable({
  documentRoot,
  preferencesStore,
  onEdit,
  onDuplicate,
  onDelete,
  onTest,
  onToggle,
  onBulkOperation,
  onBulkDelete,
  onEditApiDescription,
}) {
  const document = documentRoot;
  const window = document.defaultView;
  const elements = getDashboardElements(document, {
    "bulk-delete": "button",
    "bulk-disable": "button",
    "bulk-enable": "button",
    "empty-state": "div",
    "empty-state-description": "p",
    "empty-state-title": "strong",
    "endpoint-body": "div",
    "endpoint-controls": "div",
    "endpoint-count": "span",
    "endpoint-rows": "tbody",
    "endpoint-toggle": "button",
    "filter-enabled": "select",
    "filter-method": "select",
    "filter-status": "select",
    "filter-text": "input",
    "select-all-endpoints": "input",
    "selection-count": "strong",
    "starter-actions": "div",
  });
  const preferences = preferencesStore.get();
  const events = createDashboardEventScope();
  const rowEvents = createDashboardEventScope();
  const { makeElement, actionButton } = createDashboardDom(document, rowEvents);
  /** @type {Readonly<Record<string, string>>} */
  let apiDescriptions = {};
  /** @type {{
   * endpoints: readonly import("./dashboard-core.js").MockEndpoint[],
   * statistics: import("./dashboard-core.js").DashboardStatistics|null,
   * statisticsByEndpoint: Map<string, import("./dashboard-core.js").EndpointStatistics>,
   * selectedEndpointIds: Set<string>, collapsedEndpointGroups: Set<string>,
   * endpointSort: import("./dashboard-core.js").EndpointSort,
   * endpointsCollapsed: boolean, managementPending: boolean, pendingToggleFocus: string|null
   * }} */
  const state = {
    endpoints: [],
    statistics: null,
    statisticsByEndpoint: new Map(),
    selectedEndpointIds: new Set(),
    collapsedEndpointGroups: new Set(),
    endpointSort: { key: "name", direction: "ascending" },
    endpointsCollapsed: preferences.endpointsCollapsed,
    managementPending: false,
    pendingToggleFocus: null,
  };
  function setEndpointsCollapsed(collapsed) {
    state.endpointsCollapsed = collapsed;
    document.documentElement.dataset.endpointsCollapsed = String(collapsed);
    elements["endpoint-body"].hidden = collapsed;
    elements["endpoint-controls"].hidden = collapsed;
    elements["endpoint-toggle"].setAttribute("aria-expanded", String(!collapsed));
    const label = collapsed ? "Expand endpoints" : "Collapse endpoints";
    elements["endpoint-toggle"].setAttribute("aria-label", label);
    elements["endpoint-toggle"].title = label;
    if (collapsed) hideApiDescriptions();
    if (!collapsed) renderEndpoints();
  }

  function hideApiDescriptions() {
    /** @type {NodeListOf<HTMLElement>} */
    const tooltips = elements["endpoint-rows"].querySelectorAll(".api-description-tooltip");
    for (const tooltip of tooltips) tooltip.hidePopover();
  }

  function endpointStatistics(id) {
    return state.statisticsByEndpoint.get(id);
  }

  function visibleEndpoints() {
    return filterEndpoints(state.endpoints, {
      query: elements["filter-text"].value,
      method: elements["filter-method"].value,
      enabled: elements["filter-enabled"].value,
      statusClass: elements["filter-status"].value,
    });
  }

  function currentEndpointGroups() {
    return groupEndpointsByPath(visibleEndpoints()).map((group) => ({
      ...group,
      endpoints: sortEndpoints(group.endpoints, state.endpointSort, state.statistics?.endpoints || []),
    }));
  }

  function updateBulkSelection(visible) {
    const availableIds = new Set(state.endpoints.map((endpoint) => endpoint.id));
    for (const endpointId of state.selectedEndpointIds) {
      if (!availableIds.has(endpointId)) state.selectedEndpointIds.delete(endpointId);
    }

    const selectedVisible = visible.filter((endpoint) => state.selectedEndpointIds.has(endpoint.id)).length;
    elements["select-all-endpoints"].checked = visible.length > 0 && selectedVisible === visible.length;
    elements["select-all-endpoints"].indeterminate = selectedVisible > 0 && selectedVisible < visible.length;
    elements["select-all-endpoints"].disabled = visible.length === 0;
    elements["selection-count"].textContent = formatNumber(state.selectedEndpointIds.size);
    for (const button of [elements["bulk-enable"], elements["bulk-disable"], elements["bulk-delete"]]) {
      button.disabled = state.managementPending || state.selectedEndpointIds.size === 0;
    }
  }

  function renderEndpoints() {
    if (state.endpointsCollapsed) {
      elements["endpoint-count"].textContent = String(visibleEndpoints().length);
      return;
    }
    const focusedEndpointControl = document.activeElement?.getAttribute("data-endpoint-focus");
    const groups = currentEndpointGroups();
    const filtered = groups.flatMap((group) => group.endpoints);

    elements["endpoint-count"].textContent = String(filtered.length);
    rowEvents.clear();
    elements["endpoint-rows"].replaceChildren();
    elements["empty-state"].hidden = filtered.length !== 0;
    const hasEndpoints = state.endpoints.length !== 0;
    elements["empty-state-title"].textContent = hasEndpoints ? "No endpoints match" : "No endpoints yet";
    elements["empty-state-description"].textContent = hasEndpoints
      ? "Adjust the active filters to see configured endpoints."
      : "Load working examples or begin with a blank configuration.";
    elements["starter-actions"].hidden = hasEndpoints;
    for (const group of groups) {
      elements["endpoint-rows"].append(createEndpointGroupRow(group));
      if (!state.collapsedEndpointGroups.has(group.key)) {
        for (const endpoint of group.endpoints) elements["endpoint-rows"].append(createEndpointRow(endpoint));
      }
    }
    updateBulkSelection(filtered);
    updateSortHeaders();
    if (focusedEndpointControl) {
      /** @type {HTMLElement|null} */
      const control = document.querySelector(`[data-endpoint-focus="${CSS.escape(focusedEndpointControl)}"]`);
      control?.focus({ preventScroll: true });
    }
  }

  function updateSortHeaders() {
    /** @type {NodeListOf<HTMLButtonElement>} */
    const buttons = document.querySelectorAll("[data-sort-key]");
    for (const button of buttons) {
      const active = button.dataset.sortKey === state.endpointSort.key;
      button.parentElement.setAttribute("aria-sort", active ? state.endpointSort.direction : "none");
      button.dataset.direction = active ? state.endpointSort.direction : "none";
    }
  }

  function createEndpointGroupRow(group) {
    const row = document.createElement("tr");
    row.className = "endpoint-group-row";
    const cell = document.createElement("th");
    cell.colSpan = 8;
    cell.scope = "rowgroup";
    const button = makeElement("button", "endpoint-group-toggle");
    button.type = "button";
    button.dataset.endpointGroup = group.key;
    const expanded = !state.collapsedEndpointGroups.has(group.key);
    button.setAttribute("aria-expanded", String(expanded));
    button.setAttribute("aria-label", `${expanded ? "Collapse" : "Expand"} ${group.label}`);
    button.append(
      makeElement("span", "endpoint-group-chevron"),
      makeElement("strong", null, group.label),
      makeElement("span", "endpoint-group-count", formatNumber(group.endpoints.length))
    );
    rowEvents.listen(button, "click", () => {
      if (expanded) state.collapsedEndpointGroups.add(group.key);
      else state.collapsedEndpointGroups.delete(group.key);
      renderEndpoints();
      /** @type {HTMLButtonElement|null} */
      const toggle = document.querySelector(`[data-endpoint-group="${CSS.escape(group.key)}"]`);
      toggle?.focus();
    });
    const path = group.key === "/" ? "/" : `/${group.key}`;
    const description = apiDescriptions[path] || "";
    const header = makeElement("div", "endpoint-group-header");
    const { info, tooltip } = createDescriptionControl({
      id: `api-description-${encodeURIComponent(path)}`,
      title: path,
      description: description || "No API description yet.",
      label: `Edit API description for ${path}`,
      onActivate: () => onEditApiDescription(path, description),
    });
    info.dataset.apiDescription = path;
    info.dataset.endpointFocus = `api:${path}`;
    header.append(button, info, tooltip);
    cell.append(header);
    row.append(cell);
    return row;
  }

  /**
   * Shares plain-text previews and their listener lifecycle between API groups and endpoints.
   * @param {{id: string, title: string, description: string, label: string, detail?: string, onActivate: () => void}} options Preview content and editor action.
   * @returns {{info: HTMLButtonElement, tooltip: HTMLDivElement}} Nodes owned by the current row render.
   */
  function createDescriptionControl({ id, title, description, label, detail, onActivate }) {
    const info = makeElement("button", "api-description-button", "i");
    info.type = "button";
    info.setAttribute("aria-label", label);
    const tooltip = makeElement("div", "api-description-tooltip");
    tooltip.id = id;
    tooltip.setAttribute("role", "tooltip");
    tooltip.setAttribute("popover", "manual");
    // Configuration is untrusted text, never HTML or executable JavaScript.
    tooltip.append(makeElement("strong", null, title), makeElement("p", null, description));
    if (detail) tooltip.append(makeElement("small", "api-description-detail", detail));
    tooltip.append(makeElement("small", null, "Activate the information button to edit."));
    info.setAttribute("aria-describedby", tooltip.id);
    function showDescription() {
      const bounds = info.getBoundingClientRect();
      tooltip.style.top = `${bounds.bottom}px`;
      tooltip.style.maxHeight = "";
      tooltip.showPopover();
      const size = tooltip.getBoundingClientRect();
      const spaceBelow = window.innerHeight - bounds.bottom - 8;
      const spaceAbove = bounds.top - 8;
      const below = size.height <= spaceBelow || spaceBelow >= spaceAbove;
      // Keep the preview on-screen without covering its own activation button.
      tooltip.style.maxHeight = `${Math.max(0, Math.min(320, window.innerHeight / 2, below ? spaceBelow : spaceAbove))}px`;
      tooltip.style.top = `${below ? bounds.bottom : bounds.top - tooltip.getBoundingClientRect().height}px`;
      tooltip.style.left = `${Math.max(8, Math.min(bounds.right - size.width, window.innerWidth - size.width - 8))}px`;
    }
    rowEvents.listen(info, "mouseenter", showDescription);
    rowEvents.listen(info, "focus", showDescription);
    rowEvents.listen(info, "mouseleave", () => tooltip.hidePopover());
    rowEvents.listen(info, "blur", () => {
      if (!info.matches(":hover")) tooltip.hidePopover();
    });
    rowEvents.listen(document, "keydown", (event) => {
      if (event.key === "Escape") tooltip.hidePopover();
    });
    rowEvents.listen(info, "click", () => {
      tooltip.hidePopover();
      onActivate();
    });
    return { info, tooltip };
  }

  function createEndpointRow(endpoint) {
    const row = document.createElement("tr");
    row.className = "endpoint-row";
    row.dataset.endpointId = endpoint.id;
    const statistics = endpointStatistics(endpoint.id);

    const selectionCell = makeElement("td", "centered selection-column");
    const selection = document.createElement("input");
    selection.className = "selection-checkbox";
    selection.type = "checkbox";
    selection.checked = state.selectedEndpointIds.has(endpoint.id);
    selection.dataset.endpointFocus = `${endpoint.id}:select`;
    selection.setAttribute("aria-label", `Select ${endpoint.name}`);
    rowEvents.listen(selection, "change", () => {
      if (selection.checked) state.selectedEndpointIds.add(endpoint.id);
      else state.selectedEndpointIds.delete(endpoint.id);
      updateBulkSelection(visibleEndpoints());
    });
    selectionCell.append(selection);

    const nameCell = document.createElement("td");
    const name = makeElement("div", "endpoint-name");
    const heading = makeElement("div", "endpoint-name-heading");
    const { info, tooltip } = createDescriptionControl({
      id: `endpoint-description-${endpoint.id}`,
      title: endpoint.name,
      description: endpoint.description || "No endpoint description yet.",
      label: `Endpoint information for ${endpoint.name}`,
      detail: `Operation ID: ${endpoint.id}`,
      onActivate: () => onEdit(endpoint),
    });
    info.dataset.endpointFocus = `${endpoint.id}:info`;
    heading.append(info, makeElement("strong", null, endpoint.name));
    name.append(heading, tooltip);
    nameCell.append(name);

    const methodCell = document.createElement("td");
    const methods = makeElement("div", "method-list");
    for (const item of endpoint.methods) methods.append(makeElement("span", "method-badge", item));
    methodCell.append(methods);

    const pathCell = makeElement("td", "path-cell", getEndpointDisplayPath(endpoint.path));
    pathCell.title = endpoint.path;
    const statusCell = makeElement("td", "centered");
    const abortsConnection = endpoint.response.behavior === "abortConnection";
    const statusBadge = makeElement(
      "span",
      "status-badge",
      abortsConnection ? "DROP" : String(endpoint.response.statusCode)
    );
    if (!abortsConnection) {
      statusBadge.classList.add(`status-${Math.floor(endpoint.response.statusCode / 100)}xx`);
      const reasonPhrase =
        endpoint.response.reasonPhrase || HTTP_STATUS_REASONS.get(String(endpoint.response.statusCode));
      if (reasonPhrase) statusBadge.title = reasonPhrase;
    }
    statusCell.append(statusBadge);
    const requestsCell = makeElement("td", "centered tabular-numeric");
    requestsCell.dataset.endpointRequests = "";
    updateAttemptsCell(requestsCell, statistics);

    const enabledCell = makeElement("td", "centered");
    const switchLabel = makeElement("label", "switch");
    const toggle = document.createElement("input");
    toggle.type = "checkbox";
    toggle.checked = endpoint.enabled;
    toggle.disabled = state.managementPending;
    toggle.dataset.endpointFocus = `${endpoint.id}:enabled`;
    toggle.setAttribute("aria-label", `${endpoint.enabled ? "Disable" : "Enable"} ${endpoint.name}`);
    rowEvents.listen(toggle, "change", () => onToggle(endpoint, toggle.checked));
    switchLabel.append(toggle, makeElement("span"));
    enabledCell.append(switchLabel);

    const actionCell = document.createElement("td");
    const actions = makeElement("div", "row-actions");
    const testButton = actionButton("Test", "play", (event) => onTest(endpoint, event.currentTarget), "test");
    testButton.dataset.testEndpointId = endpoint.id;
    const editButton = actionButton("Edit", "edit", () => onEdit(endpoint), "edit");
    const duplicateButton = actionButton("Duplicate", "copy", () => onDuplicate(endpoint), "copy");
    const deleteButton = actionButton("Delete", "delete", () => onDelete(endpoint), "delete");
    /** @type {[string, HTMLButtonElement][]} */
    const actionButtons = [
      ["test", testButton],
      ["edit", editButton],
      ["duplicate", duplicateButton],
      ["delete", deleteButton],
    ];
    for (const [action, button] of actionButtons) {
      button.dataset.endpointFocus = `${endpoint.id}:${action}`;
    }
    actions.append(testButton, editButton, duplicateButton, deleteButton);
    actionCell.append(actions);

    row.append(selectionCell, pathCell, nameCell, methodCell, statusCell, requestsCell, enabledCell, actionCell);
    return row;
  }

  function updateAttemptsCell(cell, statistics) {
    const attempts = statistics?.totalRequests || 0;
    const attemptsLabel = attempts === 1 ? "attempt" : "attempts";
    const lastAttempt = formatTime(statistics?.lastRequestUtc);
    cell.textContent = formatNumber(attempts);
    cell.title = `Last attempt: ${lastAttempt}`;
    cell.setAttribute("aria-label", `${formatNumber(attempts)} ${attemptsLabel}. Last attempt: ${lastAttempt}`);
  }

  function updateEndpointStatistics() {
    if (state.endpointsCollapsed) return;
    /** @type {NodeListOf<HTMLTableRowElement>} */
    const endpointRows = elements["endpoint-rows"].querySelectorAll(".endpoint-row");
    const rows = [...endpointRows];
    const expectedIds = currentEndpointGroups()
      .filter((group) => !state.collapsedEndpointGroups.has(group.key))
      .flatMap((group) => group.endpoints.map((endpoint) => endpoint.id));
    if (
      rows.length !== expectedIds.length ||
      rows.some((row, index) => row.dataset.endpointId !== expectedIds[index])
    ) {
      renderEndpoints();
      return;
    }
    for (const row of rows) {
      const statistics = endpointStatistics(row.dataset.endpointId);
      const requestsCell = row.querySelector("[data-endpoint-requests]");
      updateAttemptsCell(requestsCell, statistics);
    }
  }

  function setPending(pending) {
    if (pending) {
      const focused = document.activeElement;
      state.pendingToggleFocus = focused.matches(".switch input") ? focused.getAttribute("data-endpoint-focus") : null;
    }
    state.managementPending = pending;
    /** @type {NodeListOf<HTMLInputElement>} */
    const toggles = elements["endpoint-rows"].querySelectorAll(".switch input");
    for (const toggle of toggles) {
      toggle.disabled = pending;
    }
    updateBulkSelection(visibleEndpoints());
    if (!pending && state.pendingToggleFocus) {
      // Native disabling blurs the toggle; restore it only if the user has not moved focus elsewhere.
      if (document.activeElement === document.body) {
        /** @type {HTMLElement|null} */
        const control = document.querySelector(`[data-endpoint-focus="${CSS.escape(state.pendingToggleFocus)}"]`);
        control?.focus({ preventScroll: true });
      }
      state.pendingToggleFocus = null;
    }
  }
  events.listen(elements["select-all-endpoints"], "change", () => {
    for (const endpoint of visibleEndpoints()) {
      if (elements["select-all-endpoints"].checked) state.selectedEndpointIds.add(endpoint.id);
      else state.selectedEndpointIds.delete(endpoint.id);
    }
    renderEndpoints();
  });
  events.listen(elements["bulk-enable"], "click", () => onBulkOperation("enable", [...state.selectedEndpointIds]));
  events.listen(elements["bulk-disable"], "click", () => onBulkOperation("disable", [...state.selectedEndpointIds]));
  events.listen(elements["bulk-delete"], "click", () => onBulkDelete([...state.selectedEndpointIds]));
  for (const filter of [
    elements["filter-text"],
    elements["filter-method"],
    elements["filter-enabled"],
    elements["filter-status"],
  ]) {
    events.listen(filter, "input", () => {
      renderEndpoints();
    });
  }
  /** @type {NodeListOf<HTMLButtonElement>} */
  const sortButtons = document.querySelectorAll("[data-sort-key]");
  for (const button of sortButtons) {
    events.listen(button, "click", () => {
      const key = /** @type {import("./dashboard-core.js").EndpointSortKey} */ (button.dataset.sortKey);
      state.endpointSort = {
        key,
        direction:
          state.endpointSort.key === key && state.endpointSort.direction === "ascending" ? "descending" : "ascending",
      };
      renderEndpoints();
    });
  }
  events.listen(elements["endpoint-toggle"], "click", () => {
    setEndpointsCollapsed(!state.endpointsCollapsed);
    preferencesStore.update({ endpointsCollapsed: state.endpointsCollapsed });
  });
  setEndpointsCollapsed(state.endpointsCollapsed);
  return {
    updateApiDescriptions(descriptions) {
      if (JSON.stringify(apiDescriptions) === JSON.stringify(descriptions || {})) return;
      apiDescriptions = descriptions || {};
      renderEndpoints();
    },
    updateEndpoints(endpoints) {
      state.endpoints = endpoints;
      updateBulkSelection(visibleEndpoints());
      renderEndpoints();
    },
    updateStatistics(statistics) {
      state.statistics = statistics;
      state.statisticsByEndpoint = new Map(statistics.endpoints.map((endpoint) => [endpoint.endpointId, endpoint]));
      updateEndpointStatistics();
    },
    setPending,
    clearSelection() {
      state.selectedEndpointIds.clear();
      renderEndpoints();
    },
    render: renderEndpoints,
    dispose() {
      hideApiDescriptions();
      events.clear();
      rowEvents.clear();
    },
  };
}
