"use strict";

import { formatMergeResult, formatNumber, formatProblem } from "./dashboard-core.js?v={{ASSET_VERSION}}";
import { createDashboardPreferencesStore } from "./dashboard-preferences.js?v={{ASSET_VERSION}}";
import { createDashboardLayoutController } from "./dashboard-layout.js?v={{ASSET_VERSION}}";
import { createDashboardSynchronizer } from "./dashboard-sync.js?v={{ASSET_VERSION}}";
import {
  createDashboardCommandRunner,
  createDashboardManagementClient,
} from "./dashboard-management.js?v={{ASSET_VERSION}}";
import { createDashboardTutorialController } from "./dashboard-tutorial.js?v={{ASSET_VERSION}}";
import { createEndpointTestRequestController } from "./dashboard-test-request.js?v={{ASSET_VERSION}}";
import { createDashboardEditorDialog } from "./dashboard-editor-dialog.js?v={{ASSET_VERSION}}";
import { createApiDescriptionEditor } from "./dashboard-api-description.js?v={{ASSET_VERSION}}";
import { createDashboardApiSecurity } from "./dashboard-api-security.js?v={{ASSET_VERSION}}";
import { createDashboardEndpointTable } from "./dashboard-endpoint-table.js?v={{ASSET_VERSION}}";
import { createDashboardStatistics } from "./dashboard-statistics.js?v={{ASSET_VERSION}}";
import { createDashboardTestBlade } from "./dashboard-test-blade.js?v={{ASSET_VERSION}}";
import {
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "./dashboard-dom.js?v={{ASSET_VERSION}}";

const elements = getDashboardElements(document, {
  "confirm-accept": "button",
  "confirm-cancel": "button",
  "confirm-dialog": "dialog",
  "confirm-message": "p",
  "confirm-title": "h2",
  "connection-status": "span",
  "create-button": "button",
  "empty-load-example-button": "button",
  "export-button": "a",
  "export-format": "select",
  "help-menu": "div",
  "help-menu-button": "button",
  "how-it-works-button": "button",
  "how-it-works-close": "button",
  "how-it-works-dialog": "dialog",
  "how-it-works-load-examples": "button",
  "how-it-works-new-endpoint": "button",
  "import-button": "button",
  "import-file": "input",
  "load-example-button": "button",
  "save-button": "button",
  "settings-button": "button",
  "settings-close": "button",
  "settings-dashboard-layout": "select",
  "settings-dialog": "dialog",
  "settings-endpoint-test-dialog-alignment": "select",
  "start-tutorial-button": "button",
  "theme-toggle": "button",
  "toast-region": "div",
  "tutorial-back": "button",
  "tutorial-backdrop": "div",
  "tutorial-card": "section",
  "tutorial-close": "button",
  "tutorial-description": "p",
  "tutorial-next": "button",
  "tutorial-progress": "span",
  "tutorial-title": "h2",
});

const API = "/__mockapi/api";
// Access storage inside the store's guarded operations; the localStorage getter itself can throw.
const dashboardPreferencesStore = createDashboardPreferencesStore({
  getItem: (key) => window.localStorage.getItem(key),
  setItem: (key, value) => window.localStorage.setItem(key, value),
});
const dashboardPreferences = dashboardPreferencesStore.get();
const pageEvents = createDashboardEventScope();
const toastTimers = new Set();
const { makeElement, createIcon } = createDashboardDom(document, pageEvents);
/** @type {import("./dashboard-sync.js").DashboardSynchronizer} */
let dashboardSynchronizer;
/** @type {{etag: string|null, dirty: boolean, pendingConfirm: (() => Promise<void>)|null, managementPending: boolean}} */
const state = { etag: null, dirty: false, pendingConfirm: null, managementPending: false };
const api = createDashboardManagementClient({ request: (url, options) => fetch(url, options) });
const apiSecurity = createDashboardApiSecurity({
  documentRoot: document,
  administratorConfigured: document.documentElement.dataset.securityAdministration === "true",
  api,
  showError: (message) => showToast(message, true),
  confirm: (message) => window.confirm(message),
  copyToClipboard,
});
const managementCommands = createDashboardCommandRunner({
  synchronize: refresh,
  onError: (error) => showToast(formatProblem(error), true),
  onPendingChange: setManagementPending,
});
const endpointEditor = createDashboardEditorDialog({
  documentRoot: document,
  createId: () => crypto.randomUUID(),
  copyToClipboard,
  onSave: saveEndpoint,
});
const apiDescriptionEditor = createApiDescriptionEditor({
  documentRoot: document,
  onSave: (path, description, etag, reportError) =>
    managementCommands.run(async () => {
      await api("/configuration/api-description", {
        method: "PUT",
        body: JSON.stringify({ path, description }),
        mutatesConfiguration: true,
        etag,
      });
      showToast("API description updated");
    }, reportError),
});
const testBlade = createDashboardTestBlade({
  documentRoot: document,
  createRequestController: () =>
    createEndpointTestRequestController({
      origin: window.location.origin,
      request: (url, options) => fetch(url, options),
      now: () => performance.now(),
      getApiKey: apiSecurity.getKey,
    }),
  copyToClipboard,
  showError: (message) => showToast(message, true),
});
const endpointTable = createDashboardEndpointTable({
  documentRoot: document,
  preferencesStore: dashboardPreferencesStore,
  onEdit: (endpoint) => endpointEditor.open(endpoint, state.etag),
  onDuplicate: (endpoint) => endpointEditor.duplicate(endpoint, state.etag),
  onDelete: requestDelete,
  onTest: testBlade.open,
  onToggle: toggleEndpoint,
  onBulkOperation: runBulkEndpointOperation,
  onBulkDelete: requestBulkDelete,
  onEditApiDescription: (path, description) => apiDescriptionEditor.open(path, description, state.etag),
});
const statisticsPanel = createDashboardStatistics({
  documentRoot: document,
  preferencesStore: dashboardPreferencesStore,
  onReset: requestStatisticsReset,
});

function showToast(message, isError = false) {
  const toast = makeElement("div", `toast${isError ? " error" : ""}`, message);
  elements["toast-region"].append(toast);
  const timer = window.setTimeout(() => {
    toastTimers.delete(timer);
    toast.remove();
  }, 4200);
  toastTimers.add(timer);
}

/** @type {import("./dashboard-core.js").CopyToClipboard} */
async function copyToClipboard(text, successMessage) {
  try {
    await navigator.clipboard.writeText(text);
    showToast(successMessage);
  } catch {
    showToast("Clipboard access was denied. Copy the text manually.", true);
  }
}

function setConnection(online) {
  const element = elements["connection-status"];
  element.className = `connection-status ${online ? "online" : "offline"}`;
  element.lastChild.textContent = online ? "Live" : "Disconnected";
  element.title = online
    ? "Dashboard connected: configuration and statistics updates are available."
    : "Dashboard disconnected: configuration and statistics updates are currently unavailable.";
}

async function refresh() {
  const result = await dashboardSynchronizer.synchronize();
  if (result.kind === "failed") throw result.error;
  if (result.kind === "inactive") {
    throw new Error(
      "Dashboard refresh was interrupted. Return to the dashboard and review the current configuration before making another change."
    );
  }
}

function renderConfiguration() {
  elements["save-button"].disabled = state.managementPending || !state.dirty;
}

function renderStaticIcons() {
  /** @type {NodeListOf<HTMLButtonElement>} */
  const buttons = document.querySelectorAll("[data-icon]");
  for (const button of buttons) {
    const icon = /** @type {import("./dashboard-dom.js").DashboardIcon} */ (button.dataset.icon);
    button.replaceChildren(createIcon(icon));
  }
}

async function toggleEndpoint(endpoint, enabled) {
  const etag = state.etag;
  await managementCommands.run(
    async () => {
      await api(`/endpoints/${endpoint.id}/enabled`, {
        method: "PUT",
        body: JSON.stringify({ enabled }),
        mutatesConfiguration: true,
        etag,
      });
    },
    (error) => {
      showToast(formatProblem(error), true);
      endpointTable.render();
    }
  );
}

async function applyBulkEndpointOperation(operation, endpointIds, etag) {
  await api("/endpoints/bulk", {
    method: "POST",
    body: JSON.stringify({ endpointIds, operation }),
    mutatesConfiguration: true,
    etag,
  });
  endpointTable.clearSelection();
  const noun = endpointIds.length === 1 ? "endpoint" : "endpoints";
  const verb = operation === "enable" ? "Enabled" : operation === "disable" ? "Disabled" : "Deleted";
  showToast(`${verb} ${formatNumber(endpointIds.length)} ${noun}`);
}

async function runBulkEndpointOperation(operation, endpointIds) {
  const etag = state.etag;
  await managementCommands.run(() => applyBulkEndpointOperation(operation, endpointIds, etag));
}

function requestBulkDelete(endpointIds) {
  const count = endpointIds.length;
  const noun = count === 1 ? "endpoint" : "endpoints";
  confirmAction(
    `Delete ${formatNumber(count)} selected ${noun}`,
    `Delete the selected ${noun}? This changes the active configuration immediately.`,
    (etag) => applyBulkEndpointOperation("delete", endpointIds, etag),
    "Delete"
  );
}

function requestDelete(endpoint) {
  confirmAction(
    "Delete endpoint",
    `Delete “${endpoint.name}”? This changes the active configuration immediately.`,
    async (etag) => {
      await api(`/endpoints/${endpoint.id}`, { method: "DELETE", mutatesConfiguration: true, etag });
      showToast("Endpoint deleted");
    }
  );
}

function confirmAction(title, message, action, acceptLabel = "Confirm", etag = state.etag) {
  elements["confirm-title"].textContent = title;
  elements["confirm-message"].textContent = message;
  // Bind confirmation to the reviewed revision, not a newer revision received while the dialog is open.
  state.pendingConfirm = () => action(etag);
  elements["confirm-accept"].textContent = acceptLabel;
  elements["confirm-dialog"].showModal();
  elements["confirm-accept"].focus();
}

async function acceptConfirmation() {
  const action = state.pendingConfirm;
  state.pendingConfirm = null;
  elements["confirm-dialog"].close();
  if (!action) return;
  await managementCommands.run(action);
}

async function importConfiguration(file) {
  const etag = state.etag;
  await managementCommands.run(async () => {
    const content = await file.text();
    let document;
    try {
      document = JSON.parse(content);
    } catch {
      throw new Error("The selected file does not contain valid JSON.");
    }
    await loadConfiguration(document, "Import configuration", "Configuration imported", etag);
  });
}

async function loadBuiltInConfiguration(name) {
  const etag = state.etag;
  await managementCommands.run(
    async () => {
      /** @type {import("./dashboard-core.js").BuiltInMergeResult} */
      const result = await api(`/configuration/${name}/merge`, {
        method: "POST",
        mutatesConfiguration: true,
        etag,
      });
      showToast(formatMergeResult(result));
    },
    (error) => {
      if (error.status !== 409 || !Array.isArray(error.problem?.conflicts)) {
        showToast(formatProblem(error), true);
        return;
      }
      const details = error.problem.conflicts
        .map((conflict) => `${conflict.builtInName} conflicts with ${conflict.existingName}`)
        .join("; ");
      confirmAction(
        "Built-in configuration conflicts",
        `${details}. No changes were made. Force update applies the built-in versions and preserves unrelated endpoints.`,
        async (reviewedEtag) => {
          /** @type {import("./dashboard-core.js").BuiltInMergeResult} */
          const result = await api(`/configuration/${name}/merge?force=true`, {
            method: "POST",
            mutatesConfiguration: true,
            etag: reviewedEtag,
          });
          showToast(formatMergeResult(result));
        },
        "Force update",
        etag
      );
    }
  );
}

async function loadConfiguration(document, title, successMessage, etag) {
  /** @type {import("./dashboard-core.js").ConfigurationValidationResult} */
  const validation = await api("/configuration/validate", { method: "POST", body: JSON.stringify(document) });
  if (!validation.isValid) {
    throw new Error(validation.errors.map((error) => `${error.path}: ${error.message}`).join(" · "));
  }
  confirmAction(
    title,
    `Replace the active configuration with ${document.endpoints.length} endpoint(s)?`,
    async (reviewedEtag) => {
      await api("/configuration/import", {
        method: "PUT",
        body: JSON.stringify(document),
        mutatesConfiguration: true,
        etag: reviewedEtag,
      });
      showToast(successMessage);
    },
    "Confirm",
    etag
  );
}

async function saveConfiguration() {
  const etag = state.etag;
  await managementCommands.run(async () => {
    await api("/configuration/save", { method: "POST", mutatesConfiguration: true, etag });
    showToast("Configuration saved");
  });
}

function setHelpMenuOpen(open) {
  elements["help-menu"].hidden = !open;
  elements["help-menu-button"].setAttribute("aria-expanded", String(open));
  /** @type {HTMLElement|null} */
  const firstItem = elements["help-menu"].querySelector('[role="menuitem"]');
  if (open) firstItem?.focus();
}

function handleHelpMenuKeydown(event) {
  if (!["ArrowDown", "ArrowUp"].includes(event.key)) return;
  event.preventDefault();
  /** @type {NodeListOf<HTMLElement>} */
  const menuItems = elements["help-menu"].querySelectorAll('[role="menuitem"]');
  const items = [...menuItems];
  const direction = event.key === "ArrowDown" ? 1 : -1;
  const currentIndex = items.findIndex((item) => item === document.activeElement);
  items[(currentIndex + direction + items.length) % items.length].focus();
}

function requestStatisticsReset(endpoint) {
  const endpointMode = endpoint !== null;
  confirmAction(
    endpointMode ? "Reset endpoint statistics" : "Reset overall statistics",
    endpointMode
      ? `Reset process-local statistics for “${endpoint?.name}”?`
      : "Reset all process-local request statistics?",
    async () => {
      await api(endpointMode ? `/statistics/endpoints/${endpoint.id}/reset` : "/statistics/reset", {
        method: "POST",
      });
      showToast(endpointMode ? "Endpoint statistics reset" : "Statistics reset");
    }
  );
}

function bindEvents() {
  pageEvents.listen(elements["create-button"], "click", () => endpointEditor.open(null, state.etag));
  pageEvents.listen(elements["help-menu-button"], "click", () => {
    setHelpMenuOpen(elements["help-menu"].hidden);
  });
  pageEvents.listen(elements["help-menu"], "keydown", handleHelpMenuKeydown);
  pageEvents.listen(elements["how-it-works-button"], "click", () => {
    setHelpMenuOpen(false);
    elements["how-it-works-dialog"].showModal();
  });
  pageEvents.listen(elements["how-it-works-dialog"], "close", () => elements["help-menu-button"].focus());
  pageEvents.listen(elements["how-it-works-close"], "click", () => elements["how-it-works-dialog"].close());
  pageEvents.listen(elements["how-it-works-load-examples"], "click", () => {
    elements["how-it-works-dialog"].close();
    loadBuiltInConfiguration("example");
  });
  pageEvents.listen(elements["how-it-works-new-endpoint"], "click", () => {
    elements["how-it-works-dialog"].close();
    endpointEditor.open(null, state.etag);
  });
  pageEvents.listen(elements["settings-button"], "click", () => {
    const preferences = dashboardPreferencesStore.get();
    elements["settings-dashboard-layout"].value = preferences.dashboardLayout;
    elements["settings-endpoint-test-dialog-alignment"].value = preferences.endpointTestDialogAlignment;
    elements["settings-dialog"].showModal();
    elements["settings-dashboard-layout"].focus();
    void apiSecurity.open();
  });
  pageEvents.listen(elements["settings-dialog"], "close", () => {
    apiSecurity.close();
    elements["settings-button"].focus();
  });
  pageEvents.listen(elements["settings-close"], "click", () => elements["settings-dialog"].close());
  pageEvents.listen(elements["settings-dashboard-layout"], "change", (event) => {
    const dashboardLayout = /** @type {import("./dashboard-preferences.js").DashboardLayout} */ (
      event.currentTarget.value
    );
    dashboardPreferencesStore.update({ dashboardLayout });
    layoutController.refresh();
  });
  pageEvents.listen(elements["settings-endpoint-test-dialog-alignment"], "change", (event) => {
    const preferences = dashboardPreferencesStore.update({
      endpointTestDialogAlignment: /** @type {import("./dashboard-preferences.js").TestBladeAlignment} */ (
        event.currentTarget.value
      ),
    });
    testBlade.setAlignment(preferences.endpointTestDialogAlignment);
  });
  pageEvents.listen(elements["load-example-button"], "click", () => loadBuiltInConfiguration("example"));
  pageEvents.listen(elements["empty-load-example-button"], "click", () => loadBuiltInConfiguration("example"));
  pageEvents.listen(elements["import-button"], "click", () => elements["import-file"].click());
  pageEvents.listen(elements["export-format"], "change", (event) => {
    const suffix = event.currentTarget.value === "native" ? "" : `/${event.currentTarget.value}`;
    elements["export-button"].href = `${API}/configuration/export${suffix}`;
  });
  pageEvents.listen(elements["import-file"], "change", () => {
    const file = elements["import-file"].files[0];
    if (file) importConfiguration(file);
    elements["import-file"].value = "";
  });
  pageEvents.listen(elements["save-button"], "click", saveConfiguration);
  pageEvents.listen(elements["confirm-cancel"], "click", () => elements["confirm-dialog"].close());
  pageEvents.listen(elements["confirm-accept"], "click", acceptConfirmation);
  pageEvents.listen(document, "keydown", (event) => {
    if (event.defaultPrevented) return;
    if (event.key === "Escape" && !elements["help-menu"].hidden) {
      setHelpMenuOpen(false);
      elements["help-menu-button"].focus();
      return;
    }
  });
  pageEvents.listen(document, "click", (event) => {
    if (event.target instanceof Element && !event.target.closest(".help-menu-shell")) setHelpMenuOpen(false);
  });
  pageEvents.listen(elements["theme-toggle"], "click", () => {
    const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    dashboardPreferencesStore.update({ theme: next });
  });
}

function setManagementPending(pending) {
  state.managementPending = pending;
  endpointEditor.setPending(pending);
  apiDescriptionEditor.setPending(pending);
  endpointTable.setPending(pending);
  for (const id of ["load-example-button", "empty-load-example-button", "import-button", "confirm-accept"]) {
    elements[id].disabled = pending;
  }
  renderConfiguration();
  if (!pending && elements["confirm-dialog"].open) elements["confirm-accept"].focus();
}

/** @type {import("./dashboard-editor-dialog.js").SaveEndpointDraft} */
function saveEndpoint(endpoint, draft, reportError) {
  return managementCommands.run(async () => {
    await api(draft.editing ? `/endpoints/${endpoint.id}` : "/endpoints", {
      method: draft.editing ? "PUT" : "POST",
      body: JSON.stringify(endpoint),
      mutatesConfiguration: true,
      etag: draft.etag,
    });
    showToast(draft.editing ? "Endpoint updated" : "Endpoint created");
  }, reportError);
}

renderStaticIcons();
const layoutController = createDashboardLayoutController({
  documentRoot: document,
  windowRoot: window,
  preferencesStore: dashboardPreferencesStore,
  createResizeObserver: (callback) => new ResizeObserver(callback),
});
testBlade.setAlignment(dashboardPreferences.endpointTestDialogAlignment);
const tutorialController = createDashboardTutorialController({
  documentRoot: document,
  elements: {
    backdrop: elements["tutorial-backdrop"],
    card: elements["tutorial-card"],
    progress: elements["tutorial-progress"],
    title: elements["tutorial-title"],
    description: elements["tutorial-description"],
    closeButton: elements["tutorial-close"],
    backButton: elements["tutorial-back"],
    nextButton: elements["tutorial-next"],
    startButton: elements["start-tutorial-button"],
  },
  preferencesStore: dashboardPreferencesStore,
  onStart: () => setHelpMenuOpen(false),
});
bindEvents();
const createEventSource = "EventSource" in window ? (path) => new EventSource(`${API}${path}`) : null;
if (!createEventSource) {
  console.warn("Server-sent events are unavailable. Dashboard updates will use polling.");
}
dashboardSynchronizer = createDashboardSynchronizer({
  request: api,
  createEventSource,
  onConfiguration(configuration) {
    state.etag = configuration.etag;
    state.dirty = configuration.hasUnsavedChanges;
    endpointTable.updateApiDescriptions(configuration.apiDescriptions);
    renderConfiguration();
  },
  onEndpoints(endpoints) {
    endpointTable.updateEndpoints(endpoints);
    statisticsPanel.updateEndpoints(endpoints);
  },
  onStatistics(statistics) {
    statisticsPanel.updateStatistics(statistics);
    endpointTable.updateStatistics(statistics);
  },
  onConnection: setConnection,
  onError: (error) => console.error(error),
});
if (document.visibilityState === "hidden") dashboardSynchronizer.setVisible(false);
pageEvents.listen(document, "visibilitychange", () => {
  dashboardSynchronizer.setVisible(document.visibilityState !== "hidden");
});
pageEvents.listen(window, "pageshow", (event) => {
  if (event.persisted) dashboardSynchronizer.setVisible(document.visibilityState !== "hidden");
});
pageEvents.listen(window, "pagehide", (event) => {
  // Downloads can fire beforeunload without leaving the page. Cached pages resume instead of being disposed.
  if (event.persisted) {
    dashboardSynchronizer.setVisible(false);
    testBlade.close();
    return;
  }
  layoutController.dispose();
  tutorialController.dispose();
  dashboardSynchronizer.dispose();
  endpointEditor.dispose();
  apiDescriptionEditor.dispose();
  endpointTable.dispose();
  statisticsPanel.dispose();
  testBlade.dispose();
  apiSecurity.dispose();
  for (const timer of toastTimers) window.clearTimeout(timer);
  toastTimers.clear();
  elements["toast-region"].replaceChildren();
  pageEvents.clear();
});
dashboardSynchronizer.start().then(() => {
  tutorialController.startIfNeeded(window.location.search);
});
