"use strict";

import { formatProblem } from "./dashboard-core.js?v={{ASSET_VERSION}}";
import { createDashboardEventScope, getDashboardElements } from "./dashboard-dom.js?v={{ASSET_VERSION}}";

/**
 * Owns the API description draft and captured revision, independent of live table updates.
 * @param {object} options Dependencies.
 * @param {Document} options.documentRoot Dashboard document.
 * @param {(path: string, description: string, etag: string|null, reportError: import("./dashboard-management.js").ReportManagementError) => Promise<import("./dashboard-management.js").CommandResult>} options.onSave Revision-protected command.
 * @returns {{open: (path: string, description: string, etag: string|null) => void, setPending: (pending: boolean) => void, dispose: () => void}} Dialog lifecycle; disposal invalidates pending completions and removes listeners.
 */
export function createApiDescriptionEditor({ documentRoot: document, onSave }) {
  const elements = getDashboardElements(document, {
    "api-description-dialog": "dialog",
    "api-description-title": "h2",
    "api-description-form": "form",
    "api-description-text": "textarea",
    "api-description-error": "div",
    "api-description-cancel": "button",
    "api-description-apply": "button",
  });
  const events = createDashboardEventScope();
  const dialog = elements["api-description-dialog"];
  let path = "/";
  /** @type {string|null} */
  let etag = null;
  let generation = 0;
  let disposed = false;
  let pending = false;

  events.listen(elements["api-description-cancel"], "click", () => dialog.close());
  events.listen(dialog, "close", () => {
    if (dialog.open) return;
    generation++;
    /** @type {HTMLElement|null} */
    const trigger = document.querySelector(`[data-api-description="${CSS.escape(path)}"]`);
    const fallback = document.getElementById("create-button");
    (trigger || fallback)?.focus();
  });
  events.listen(elements["api-description-form"], "submit", async (event) => {
    event.preventDefault();
    if (pending || disposed || !dialog.open) return;
    const draftGeneration = generation;
    const result = await onSave(path, elements["api-description-text"].value, etag, (error) => {
      if (disposed || generation !== draftGeneration || !dialog.open) return;
      elements["api-description-error"].textContent = formatProblem(error);
      elements["api-description-error"].hidden = false;
      elements["api-description-error"].focus();
    });
    if (!disposed && generation === draftGeneration && result.kind === "completed") dialog.close();
  });

  return {
    open(groupPath, description, revision) {
      if (disposed) return;
      generation++;
      path = groupPath;
      etag = revision;
      elements["api-description-title"].textContent = `API description: ${path}`;
      elements["api-description-text"].value = description;
      elements["api-description-error"].hidden = true;
      elements["api-description-error"].textContent = "";
      dialog.showModal();
      elements["api-description-text"].focus();
    },
    setPending(value) {
      if (disposed) return;
      pending = value;
      elements["api-description-apply"].disabled = value;
    },
    dispose() {
      disposed = true;
      generation++;
      events.clear();
      dialog.close();
    },
  };
}
