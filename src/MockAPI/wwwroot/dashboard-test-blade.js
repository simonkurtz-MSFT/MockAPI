"use strict";

import { formatTestResponseDetails } from "./dashboard-core.js?v={{ASSET_VERSION}}";
import { formatResponseHeaders } from "./dashboard-test-request.js?v={{ASSET_VERSION}}";
import {
  createDashboardDom,
  createDashboardEventScope,
  getDashboardElements,
} from "./dashboard-dom.js?v={{ASSET_VERSION}}";

/**
 * @typedef {object} DashboardTestBlade
 * @property {(endpoint: import("./dashboard-core.js").MockEndpoint, trigger: HTMLElement) => void} open Starts a fresh per-opening request owner and focuses the method control.
 * @property {() => void} close Cancels the batch, clears request/response text, and restores focus to the current endpoint row.
 * @property {(alignment: import("./dashboard-preferences.js").TestBladeAlignment) => void} setAlignment Applies the saved blade placement.
 * @property {() => void} dispose Closes and permanently detaches listeners; late completions and future opens are ignored.
 */
/**
 * Owns blade visibility, request batches, cancellation, response presentation, and focus restoration.
 * Request state is never persisted. Each opening gets a fresh request controller; obsolete completions are ignored.
 * @param {object} options Dependencies.
 * @param {Document} options.documentRoot Dashboard document.
 * @param {() => import("./dashboard-test-request.js").EndpointTestRequestController} options.createRequestController Per-opening request owner.
 * @param {import("./dashboard-core.js").CopyToClipboard} options.copyToClipboard Notification-owning clipboard callback.
 * @param {(message: string) => void} options.showError Presents request validation and unexpected failures.
 * @returns {DashboardTestBlade} Request, focus, and transient content owner.
 * @throws {Error} Required dashboard markup is missing or has an incorrect tag.
 */
export function createDashboardTestBlade({ documentRoot, createRequestController, copyToClipboard, showError }) {
  const document = documentRoot;
  const window = document.defaultView;
  const elements = getDashboardElements(document, {
    "test-blade": "aside",
    "test-blade-backdrop": "button",
    "test-blade-close": "button",
    "test-blade-context": "p",
    "test-blade-shell": "div",
    "test-blade-title": "h2",
    "test-cancel": "button",
    "test-copy-response": "button",
    "test-copy-url": "button",
    "test-method": "select",
    "test-path": "input",
    "test-request-body": "textarea",
    "test-request-count": "input",
    "test-request-headers": "textarea",
    "test-response-body": "pre",
    "test-response-headers": "pre",
    "test-response-status": "dd",
    "test-response-time": "dd",
    "test-response-url": "dd",
    "test-send": "button",
    "test-status": "p",
  });
  const events = createDashboardEventScope();
  /** @type {{testTrigger: HTMLElement|null, testEndpointId: string|null}} */
  const state = { testTrigger: null, testEndpointId: null };
  /** @type {import("./dashboard-test-request.js").EndpointTestRequestController|null} */
  let testRequestController = null;
  let generation = 0;
  let sending = false;
  let disposed = false;
  /** @type {DashboardTestBlade["open"]} */
  function openTestBlade(endpoint, trigger) {
    if (disposed) return;
    generation++;
    testRequestController?.cancel();
    testRequestController = createRequestController();
    sending = false;
    elements["test-send"].disabled = false;
    elements["test-cancel"].disabled = true;
    state.testTrigger = trigger;
    state.testEndpointId = endpoint.id;
    elements["test-blade-title"].textContent = endpoint.name;
    elements["test-blade-context"].textContent = endpoint.path;
    elements["test-method"].replaceChildren(
      ...endpoint.methods.map((method) => {
        const option = document.createElement("option");
        option.value = method;
        option.textContent = method;
        return option;
      })
    );
    elements["test-path"].value = endpoint.path;
    elements["test-request-count"].value = String(endpoint.requestCount ?? 1);
    elements["test-request-headers"].value = "";
    elements["test-request-body"].value = "";
    resetTestResponse();
    elements["test-blade-shell"].hidden = false;
    document.body.classList.add("blade-open");
    elements["test-method"].focus();
  }

  function closeTestBlade() {
    generation++;
    testRequestController?.cancel();
    testRequestController = null;
    sending = false;
    elements["test-blade-shell"].hidden = true;
    document.body.classList.remove("blade-open");
    /** @type {HTMLButtonElement|null} */
    const currentTrigger = state.testEndpointId
      ? document.querySelector(`[data-test-endpoint-id="${CSS.escape(state.testEndpointId)}"]`)
      : null;
    (state.testTrigger?.isConnected ? state.testTrigger : currentTrigger)?.focus();
    state.testTrigger = null;
    state.testEndpointId = null;
    elements["test-request-headers"].value = "";
    elements["test-request-body"].value = "";
    resetTestResponse();
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

  async function sendTestRequest() {
    if (disposed || !testRequestController) return;
    if (sending) {
      showError("A request is already in progress.");
      return;
    }
    if (!elements["test-request-count"].reportValidity()) return;
    const requestGeneration = generation;
    const controller = testRequestController;
    sending = true;
    try {
      const requestCount = Number(elements["test-request-count"].value);
      const request = {
        method: elements["test-method"].value,
        path: elements["test-path"].value,
        headerLines: elements["test-request-headers"].value,
        body: elements["test-request-body"].value,
      };
      elements["test-send"].disabled = true;
      elements["test-cancel"].disabled = false;
      elements["test-status"].textContent =
        requestCount === 1 ? "Sending request…" : `Sending ${requestCount} requests…`;

      let completedCount = 0;
      /** @type {import("./dashboard-test-request.js").EndpointTestResult} */
      let result;
      for (let requestNumber = 1; requestNumber <= requestCount; requestNumber += 1) {
        result = await controller.send(request);
        // Closing, reopening, or disposal ends this batch even if an abort races a completed response.
        if (disposed || generation !== requestGeneration) return;
        if (result.kind !== "completed") break;
        completedCount = requestNumber;
      }

      if (result.kind === "validationError" || result.kind === "busy") {
        showError(result.message);
        elements["test-status"].textContent = "Request not sent.";
      } else if (result.kind === "cancelled") {
        elements["test-status"].textContent =
          completedCount === 0
            ? "Request cancelled."
            : `Request cancelled after ${completedCount} of ${requestCount} requests completed.`;
      } else if (result.kind === "networkError") {
        const progress = completedCount === 0 ? "" : ` after ${completedCount} of ${requestCount} requests completed`;
        elements["test-status"].textContent = `Network error${progress}: ${result.message}`;
      } else {
        const responseHeaders = formatResponseHeaders(result.headers);
        elements["test-response-status"].textContent = `${result.status} ${result.statusText}`.trim();
        elements["test-response-time"].textContent = `${result.elapsedMilliseconds.toFixed(1)} ms`;
        elements["test-response-url"].textContent = result.url;
        elements["test-response-headers"].textContent = responseHeaders || "No response headers.";
        elements["test-response-body"].textContent = result.body || "Empty response body.";
        elements["test-status"].textContent =
          requestCount === 1
            ? `Request completed with HTTP ${result.status}.`
            : `${requestCount} requests completed; final response HTTP ${result.status}.`;
        elements["test-copy-response"].disabled = false;
      }
    } catch (error) {
      if (!disposed && generation === requestGeneration) {
        showError(error.message);
        elements["test-status"].textContent = `Request failed: ${error.message}`;
      }
    } finally {
      if (!disposed && generation === requestGeneration) {
        sending = false;
        elements["test-send"].disabled = false;
        elements["test-cancel"].disabled = true;
      }
    }
  }

  function testResponseText() {
    return formatTestResponseDetails({
      status: elements["test-response-status"].textContent,
      elapsed: elements["test-response-time"].textContent,
      url: elements["test-response-url"].textContent,
      headers: elements["test-response-headers"].textContent,
      body: elements["test-response-body"].textContent,
    });
  }

  function trapBladeFocus(event) {
    if (event.key !== "Tab") return;
    /** @type {NodeListOf<HTMLElement>} */
    const controls = elements["test-blade"].querySelectorAll(
      "button:not(:disabled), input, select, textarea, [tabindex='0']"
    );
    const focusable = [...controls];
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  function applyEndpointTestDialogAlignment(alignment) {
    elements["test-blade-shell"].dataset.alignment = alignment;
  }
  events.listen(elements["test-blade-close"], "click", closeTestBlade);
  events.listen(elements["test-blade-backdrop"], "click", closeTestBlade);
  events.listen(elements["test-blade"], "keydown", trapBladeFocus);
  events.listen(elements["test-send"], "click", sendTestRequest);
  events.listen(elements["test-cancel"], "click", () => testRequestController?.cancel());
  events.listen(elements["test-copy-url"], "click", () => {
    copyToClipboard(new URL(elements["test-path"].value, window.location.origin).href, "Request URL copied");
  });
  events.listen(elements["test-copy-response"], "click", () => {
    copyToClipboard(testResponseText(), "Response details copied");
  });
  events.listen(document, "keydown", (event) => {
    if (event.defaultPrevented || event.key !== "Escape" || elements["test-blade-shell"].hidden) return;
    event.preventDefault();
    closeTestBlade();
  });
  return {
    open: openTestBlade,
    close: closeTestBlade,
    setAlignment: applyEndpointTestDialogAlignment,
    dispose() {
      disposed = true;
      events.clear();
      closeTestBlade();
    },
  };
}
