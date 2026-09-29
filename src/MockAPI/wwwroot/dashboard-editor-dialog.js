"use strict";

import { checkJsonBody, formatJsonBody, formatProblem } from "./dashboard-core.js?v={{ASSET_VERSION}}";
import {
  buildEndpointDefinition,
  createEndpointEditorValues,
  ENDPOINT_METHODS,
  getResponseEditorState,
  HTTP_STATUSES,
  HTTP_STATUS_REASONS,
} from "./dashboard-endpoint-editor.js?v={{ASSET_VERSION}}";
import {
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "./dashboard-dom.js?v={{ASSET_VERSION}}";

/**
 * @typedef {{editing: boolean, etag: string|null}} EndpointEditorRevision
 * Captured when opening the draft; live updates must not replace this revision.
 */
/**
 * @typedef {(endpoint: import("./dashboard-core.js").MockEndpoint, revision: EndpointEditorRevision,
 * reportError: import("./dashboard-management.js").ReportManagementError) =>
 * Promise<import("./dashboard-management.js").CommandResult>} SaveEndpointDraft
 * The command owner reports failures and resolves an explicit outcome through authoritative refresh.
 */
/**
 * @typedef {object} DashboardEditorDialog
 * @property {(endpoint: import("./dashboard-endpoint-editor.js").EndpointDraft|null, etag: string|null) => void} open Opens a detached draft; null starts a new endpoint.
 * @property {(endpoint: import("./dashboard-core.js").MockEndpoint, etag: string|null) => void} duplicate Creates a new-ID draft from an existing endpoint.
 * @property {(pending: boolean) => void} setPending Disables submission for a page-level management operation.
 * @property {() => void} dispose Closes the dialog, removes listeners, and invalidates saves. Future opens and pending updates are ignored.
 */
/**
 * Owns endpoint drafts, their reviewed ETag, form validation, and dialog listeners.
 * A save that finishes after closing/reopening cannot close or report errors into the newer draft.
 * @param {object} options Dependencies.
 * @param {Document} options.documentRoot Dashboard document.
 * @param {() => string} options.createId Stable-ID factory for new endpoints.
 * @param {import("./dashboard-core.js").CopyToClipboard} options.copyToClipboard Notification-owning clipboard callback.
 * @param {SaveEndpointDraft} options.onSave Revision-protected command runner.
 * @returns {DashboardEditorDialog} Draft and dialog owner.
 * @throws {Error} Required dashboard markup is missing or has an incorrect tag.
 */
export function createDashboardEditorDialog({ documentRoot, createId, copyToClipboard, onSave }) {
  const document = documentRoot;
  const window = document.defaultView;
  const elements = getDashboardElements(document, {
    "add-header": "button",
    "check-field-body": "button",
    "check-field-success-body": "button",
    "copy-url": "button",
    "dialog-title": "h2",
    "endpoint-dialog": "dialog",
    "endpoint-form": "form",
    "field-behavior": "select",
    "field-body": "textarea",
    "field-body-json-status": "p",
    "field-content-type": "input",
    "field-description": "textarea",
    "field-enabled": "input",
    "field-name": "input",
    "field-path": "input",
    "field-rate-limit-enabled": "input",
    "field-reason": "input",
    "field-request-limit": "input",
    "field-status": "select",
    "field-status-other": "input",
    "field-success-body": "textarea",
    "field-success-body-json-status": "p",
    "field-success-content-type": "input",
    "field-success-status": "input",
    "field-window-seconds": "input",
    "form-error": "div",
    "header-rows": "div",
    "method-options": "div",
    "rate-limit-fields": "fieldset",
  });
  const events = createDashboardEventScope();
  const { makeElement, actionButton } = createDashboardDom(document, events);
  /** @type {{editingId: string|null, editingEtag: string|null, editingRequestCount: number|null}} */
  const state = { editingId: null, editingEtag: null, editingRequestCount: null };
  let generation = 0;
  let disposed = false;
  /** @type {string|null} */
  let returnFocusKey = null;
  function buildMethodOptions() {
    elements["method-options"].replaceChildren();
    for (const method of ENDPOINT_METHODS) {
      const label = document.createElement("label");
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.name = "method";
      checkbox.value = method;
      label.append(checkbox, makeElement("span", null, method));
      elements["method-options"].append(label);
    }
  }

  function buildStatusOptions() {
    const select = elements["field-status"];
    select.replaceChildren();
    for (const statusClass of [1, 2, 3, 4, 5]) {
      const group = document.createElement("optgroup");
      group.label = `${statusClass}xx`;
      for (const [code, reason] of HTTP_STATUSES.filter(([status]) => Math.floor(status / 100) === statusClass)) {
        const option = document.createElement("option");
        option.value = String(code);
        option.textContent = `${code} ${reason}`;
        group.append(option);
      }
      select.append(group);
    }
    const other = document.createElement("option");
    other.value = "other";
    other.textContent = "Other…";
    select.append(other);
  }

  function setStatusValue(statusCode) {
    const value = String(statusCode || 200);
    const standard = HTTP_STATUS_REASONS.has(value);
    elements["field-status"].value = standard ? value : "other";
    elements["field-status-other"].value = standard ? "" : value;
    elements["field-status-other"].hidden = standard;
    elements["field-status-other"].required = !standard;
  }

  function selectedStatusCode() {
    return elements["field-status"].value === "other"
      ? Number(elements["field-status-other"].value)
      : Number(elements["field-status"].value);
  }

  function updateStatusSelection() {
    const value = elements["field-status"].value;
    const other = value === "other";
    elements["field-status-other"].hidden = !other;
    elements["field-status-other"].required = other;
    if (other) {
      elements["field-reason"].value = "";
      elements["field-status-other"].focus();
    } else {
      elements["field-reason"].value = HTTP_STATUS_REASONS.get(value);
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
    const remove = actionButton("Remove header", "remove", null, "delete");
    remove.dataset.removeHeader = "";
    row.append(nameInput, valueInput, remove);
    elements["header-rows"].append(row);
  }

  function formatResponseBody() {
    elements["field-body"].value = formatJsonBody(elements["field-content-type"].value, elements["field-body"].value);
  }

  function checkResponseBodyJson(contentTypeElement, bodyElement, statusElement) {
    const result = checkJsonBody(contentTypeElement.value, bodyElement.value);
    if (result.kind === "valid") bodyElement.value = formatJsonBody(contentTypeElement.value, bodyElement.value);
    bodyElement.setAttribute("aria-invalid", String(result.kind === "invalid"));
    statusElement.className = `json-status ${result.kind}`;
    statusElement.textContent = result.message;
    return result;
  }

  function validateJsonResponseField(contentTypeElement, bodyElement, statusElement, fieldLabel) {
    if (bodyElement.disabled) return true;

    const result = checkResponseBodyJson(contentTypeElement, bodyElement, statusElement);
    if (result.kind !== "invalid") return true;

    showFormError(`${fieldLabel} contains malformed JSON.`, bodyElement);
    return false;
  }

  function resetJsonCheck(bodyElement, statusElement) {
    bodyElement.removeAttribute("aria-invalid");
    statusElement.className = "json-status";
    statusElement.textContent = "";
  }

  /** @type {DashboardEditorDialog["open"]} */
  function openEndpointDialog(endpoint, etag) {
    if (disposed) return;
    generation++;
    returnFocusKey = document.activeElement?.getAttribute("data-endpoint-focus") ?? null;
    const values = createEndpointEditorValues(endpoint);
    state.editingId = values.id;
    state.editingEtag = etag;
    state.editingRequestCount = values.requestCount;
    elements["dialog-title"].textContent = endpoint ? "Edit endpoint" : "New endpoint";
    elements["form-error"].hidden = true;
    elements["field-name"].value = values.name;
    elements["field-description"].value = values.description;
    elements["field-path"].value = values.path;
    elements["field-behavior"].value = values.behavior;
    setStatusValue(values.statusCode);
    elements["field-reason"].value = values.reasonPhrase;
    elements["field-content-type"].value = values.contentType;
    elements["field-body"].value = values.body;
    elements["field-rate-limit-enabled"].checked = values.rateLimitEnabled;
    elements["field-request-limit"].value = String(values.requestLimit);
    elements["field-window-seconds"].value = String(values.windowSeconds);
    elements["field-success-status"].value = String(values.successStatusCode);
    elements["field-success-content-type"].value = values.successContentType;
    elements["field-success-body"].value = values.successBody;
    formatResponseBody();
    resetJsonCheck(elements["field-body"], elements["field-body-json-status"]);
    resetJsonCheck(elements["field-success-body"], elements["field-success-body-json-status"]);
    elements["field-enabled"].checked = values.enabled;
    for (const input of elements["method-options"].querySelectorAll("input")) {
      input.checked = values.methods.includes(input.value);
    }
    elements["header-rows"].replaceChildren();
    for (const header of values.headers) addHeaderRow(header.name, header.values);
    updateResponseFields();
    elements["endpoint-dialog"].showModal();
    elements["field-name"].focus();
  }

  function readEndpointForm() {
    /** @type {NodeListOf<HTMLInputElement>} */
    const checkedMethods = elements["method-options"].querySelectorAll("input:checked");
    const methods = [...checkedMethods].map((input) => input.value);
    const headers = [];
    for (const row of elements["header-rows"].children) {
      const inputs = row.querySelectorAll("input");
      headers.push({ name: inputs[0].value, values: inputs[1].value.split("\n") });
    }
    /** @type {import("./dashboard-endpoint-editor.js").EndpointEditorValues} */
    const values = {
      id: state.editingId,
      name: elements["field-name"].value,
      description: elements["field-description"].value,
      enabled: elements["field-enabled"].checked,
      methods,
      requestCount: state.editingRequestCount,
      path: elements["field-path"].value,
      behavior: /** @type {import("./dashboard-core.js").MockResponse["behavior"]} */ (
        elements["field-behavior"].value
      ),
      statusCode: selectedStatusCode(),
      reasonPhrase: elements["field-reason"].value,
      contentType: elements["field-content-type"].value,
      body: elements["field-body"].value,
      headers,
      rateLimitEnabled: elements["field-rate-limit-enabled"].checked,
      requestLimit: Number(elements["field-request-limit"].value),
      windowSeconds: Number(elements["field-window-seconds"].value),
      successStatusCode: Number(elements["field-success-status"].value),
      successContentType: elements["field-success-content-type"].value,
      successBody: elements["field-success-body"].value,
    };

    return buildEndpointDefinition(values, createId);
  }

  function updateResponseFields() {
    const editorState = getResponseEditorState(
      /** @type {import("./dashboard-core.js").MockResponse["behavior"]} */ (elements["field-behavior"].value),
      elements["field-rate-limit-enabled"].checked
    );
    for (const control of [
      elements["field-status"],
      elements["field-status-other"],
      elements["field-reason"],
      elements["field-content-type"],
      elements["field-body"],
      elements["check-field-body"],
      elements["add-header"],
      elements["field-rate-limit-enabled"],
    ]) {
      control.disabled = editorState.responseDisabled;
    }
    elements["field-status"].required = !editorState.responseDisabled;
    elements["rate-limit-fields"].hidden = !editorState.rateLimitVisible;
    /** @type {NodeListOf<HTMLInputElement|HTMLTextAreaElement>} */
    const rateLimitControls = elements["rate-limit-fields"].querySelectorAll("input, textarea");
    for (const control of rateLimitControls) {
      control.disabled = !editorState.rateLimitVisible;
      control.required = editorState.rateLimitVisible;
    }
    /** @type {NodeListOf<HTMLInputElement|HTMLButtonElement>} */
    const headerControls = elements["header-rows"].querySelectorAll("input, button");
    for (const input of headerControls) {
      input.disabled = editorState.responseDisabled;
    }
  }

  async function submitEndpoint(event) {
    event.preventDefault();
    const endpoint = readEndpointForm();
    if (endpoint.methods.length === 0) {
      const firstMethod = elements["method-options"].querySelector("input");
      showFormError("Select at least one HTTP method.", firstMethod);
      return;
    }
    if (
      !validateJsonResponseField(
        elements["field-content-type"],
        elements["field-body"],
        elements["field-body-json-status"],
        "Response body"
      ) ||
      !validateJsonResponseField(
        elements["field-success-content-type"],
        elements["field-success-body"],
        elements["field-success-body-json-status"],
        "Success response body"
      )
    ) {
      return;
    }
    const editing = Boolean(state.editingId);
    const etag = state.editingEtag;
    const draftGeneration = generation;
    const result = await onSave(endpoint, { editing, etag }, (error) => {
      if (!disposed && generation === draftGeneration && elements["endpoint-dialog"].open) {
        showFormError(formatProblem(error));
      }
    });
    if (!disposed && generation === draftGeneration && result.kind === "completed") {
      elements["endpoint-dialog"].close();
    }
  }

  function showFormError(message, focusTarget = elements["form-error"]) {
    elements["form-error"].textContent = message;
    elements["form-error"].hidden = false;
    focusTarget.focus();
  }

  /** @type {DashboardEditorDialog["duplicate"]} */
  function duplicateEndpoint(endpoint, etag) {
    if (disposed) return;
    /** @type {import("./dashboard-endpoint-editor.js").EndpointDraft} */
    const copy = structuredClone(endpoint);
    copy.id = null;
    copy.name = `${copy.name} copy`;
    copy.path = `${copy.path}-copy`;
    state.editingId = null;
    openEndpointDialog(copy, etag);
    state.editingId = null;
    elements["dialog-title"].textContent = "Duplicate endpoint";
  }
  events.listen(elements["endpoint-form"], "submit", submitEndpoint);
  events.listen(elements["field-behavior"], "change", updateResponseFields);
  events.listen(elements["field-rate-limit-enabled"], "change", updateResponseFields);
  events.listen(elements["field-body"], "blur", formatResponseBody);
  events.listen(elements["field-content-type"], "change", formatResponseBody);
  events.listen(elements["field-body"], "input", () =>
    resetJsonCheck(elements["field-body"], elements["field-body-json-status"])
  );
  events.listen(elements["field-content-type"], "input", () =>
    resetJsonCheck(elements["field-body"], elements["field-body-json-status"])
  );
  events.listen(elements["field-success-body"], "input", () =>
    resetJsonCheck(elements["field-success-body"], elements["field-success-body-json-status"])
  );
  events.listen(elements["field-success-content-type"], "input", () =>
    resetJsonCheck(elements["field-success-body"], elements["field-success-body-json-status"])
  );
  events.listen(elements["check-field-body"], "click", () =>
    checkResponseBodyJson(elements["field-content-type"], elements["field-body"], elements["field-body-json-status"])
  );
  events.listen(elements["check-field-success-body"], "click", () =>
    checkResponseBodyJson(
      elements["field-success-content-type"],
      elements["field-success-body"],
      elements["field-success-body-json-status"]
    )
  );
  events.listen(elements["field-status"], "change", updateStatusSelection);
  events.listen(elements["add-header"], "click", () => addHeaderRow());
  events.listen(elements["header-rows"], "click", (event) => {
    if (!(event.target instanceof document.defaultView.Element)) return;
    const remove = event.target.closest("[data-remove-header]");
    if (remove) remove.closest(".header-row").remove();
  });
  events.listen(elements["copy-url"], "click", () => {
    copyToClipboard(new URL(elements["field-path"].value, window.location.origin).href, "Endpoint URL copied");
  });
  for (const button of document.querySelectorAll("[data-close]")) {
    events.listen(button, "click", () => elements["endpoint-dialog"].close());
  }
  events.listen(elements["endpoint-dialog"], "close", () => {
    if (!elements["endpoint-dialog"].open) {
      generation++;
      if (returnFocusKey) {
        // Saving or live updates can replace the original row while the dialog is open.
        /** @type {HTMLElement|null} */
        const trigger = document.querySelector(`[data-endpoint-focus="${CSS.escape(returnFocusKey)}"]`);
        (trigger || document.getElementById("create-button"))?.focus({ preventScroll: true });
        returnFocusKey = null;
      }
    }
  });
  buildMethodOptions();
  buildStatusOptions();
  return {
    open: openEndpointDialog,
    duplicate: duplicateEndpoint,
    setPending(pending) {
      if (disposed) return;
      /** @type {HTMLButtonElement} */
      const submit = elements["endpoint-form"].querySelector('[type="submit"]');
      submit.disabled = pending;
    },
    dispose() {
      disposed = true;
      generation++;
      events.clear();
      elements["endpoint-dialog"].close();
    },
  };
}
