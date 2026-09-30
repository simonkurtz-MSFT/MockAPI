"use strict";

import { createDashboardEventScope, getDashboardElements } from "./dashboard-dom.js?v={{ASSET_VERSION}}";

/**
 * Owns administrator-only security Settings and the memory-only key used by endpoint tests.
 *
 * @param {object} dependencies Controller dependencies.
 * @param {Document} dependencies.documentRoot Dashboard document.
 * @param {boolean} dependencies.administratorConfigured Whether the host has configured administrative credentials.
 * @param {import("./dashboard-management.js").ManagementClient} dependencies.api Management transport.
 * @param {(message: string) => void} dependencies.showError Error presenter.
 * @param {(message: string) => boolean} dependencies.confirm Confirmation before rotation or disabling protection.
 * @param {import("./dashboard-core.js").CopyToClipboard} dependencies.copyToClipboard Clipboard boundary.
 * @returns {{open: () => Promise<void>, close: () => void, getKey: () => string, dispose: () => void}} Controller lifecycle and key provider.
 */
export function createDashboardApiSecurity({
  documentRoot,
  administratorConfigured,
  api,
  showError,
  confirm,
  copyToClipboard,
}) {
  const elements = getDashboardElements(documentRoot, {
    "settings-api-security-status": "p",
    "settings-api-security-summary": "span",
    "settings-api-security-enabled": "input",
    "settings-api-security-apply": "button",
    "settings-api-key": "input",
    "settings-api-key-error": "p",
    "settings-api-key-generate": "button",
    "settings-api-key-copy": "button",
  });
  const events = createDashboardEventScope();
  const status = elements["settings-api-security-status"];
  const summary = elements["settings-api-security-summary"];
  const enabled = elements["settings-api-security-enabled"];
  const apply = elements["settings-api-security-apply"];
  const input = elements["settings-api-key"];
  const keyError = elements["settings-api-key-error"];
  const generate = elements["settings-api-key-generate"];
  const copy = elements["settings-api-key-copy"];
  /** @type {import("./dashboard-core.js").ApiSecurityStatus|null} */
  let settings = null;
  let key = "";
  let generation = 0;
  let busy = false;
  let disposed = false;

  function setStatus(title, state, message) {
    summary.textContent = title;
    summary.dataset.state = state;
    status.textContent = message;
  }

  function setKeyError(message) {
    input.setCustomValidity(message);
    input.setAttribute("aria-invalid", String(Boolean(message)));
    keyError.textContent = message;
    keyError.hidden = !message;
  }

  function setControls() {
    enabled.disabled = busy || settings === null;
    apply.disabled = busy || settings === null;
    generate.disabled = busy || settings === null;
    input.disabled = busy;
    copy.disabled = busy || !key;
    generate.textContent = settings?.configured ? "Rotate key" : "Generate key";
  }

  async function open() {
    if (disposed) return;
    const capturedGeneration = ++generation;
    settings = null;
    input.value = key;
    setKeyError("");
    setStatus("Loading", "loading", "Loading API security settings...");
    setControls();
    if (!administratorConfigured) {
      setStatus(
        "Admin required",
        "warning",
        "Configure dashboard administrator credentials to generate keys or change protection. You can enter an existing key for dashboard tests."
      );
      return;
    }
    try {
      /** @type {import("./dashboard-core.js").ApiSecurityStatus} */
      const result = await api("/security/");
      if (disposed || capturedGeneration !== generation) return;
      settings = result;
      enabled.checked = result.enabled;
      if (!result.enabled) {
        setStatus("Protection off", "warning", "Protection disabled. Mock endpoints accept unauthenticated calls.");
      } else if (!result.configured) {
        setStatus(
          "Key needed",
          "warning",
          "API key required. Mock calls are blocked until an administrator generates a key."
        );
      } else {
        setStatus(
          "Protection on",
          "protected",
          "API key required. Enter the existing key for dashboard tests, or generate a replacement."
        );
      }
    } catch (error) {
      if (disposed || capturedGeneration !== generation) return;
      setStatus("Unable to load", "error", error.message);
      if (error.status !== 403) showError(error.message);
    } finally {
      if (!disposed && capturedGeneration === generation) setControls();
    }
  }

  async function update(rotate) {
    if (busy || settings === null) return;
    if (
      rotate &&
      settings.configured &&
      !confirm("Generate a new API key? The previous key will stop working immediately.")
    )
      return;
    if (
      !rotate &&
      !enabled.checked &&
      !confirm("Disable API-key protection? Anyone able to reach this instance can invoke its mock endpoints.")
    )
      return;
    busy = true;
    setControls();
    const capturedGeneration = generation;
    try {
      /** @type {import("./dashboard-management.js").ManagementRequestOptions} */
      const options = {
        method: rotate ? "POST" : "PUT",
        ...(rotate ? {} : { body: JSON.stringify({ enabled: enabled.checked }) }),
        mutatesConfiguration: true,
        etag: settings.etag,
      };
      if (rotate) {
        /** @type {import("./dashboard-core.js").ApiKeyCreated} */
        const result = await api("/security/key", options);
        if (disposed || capturedGeneration !== generation) return;
        key = result.key;
        input.value = key;
      } else {
        await api("/security/", options);
        if (disposed || capturedGeneration !== generation) return;
      }
      const refreshedGeneration = generation + 1;
      await open();
      if (rotate && settings !== null && !disposed && refreshedGeneration === generation)
        setStatus(
          "Key generated",
          "protected",
          "New key generated. Copy it now; the server cannot show it again. Dashboard tests use it automatically."
        );
    } catch (error) {
      if (!disposed && capturedGeneration === generation) {
        setStatus("Update failed", "error", error.message);
        showError(error.message);
      }
    } finally {
      busy = false;
      if (!disposed) setControls();
    }
  }

  events.listen(input, "input", () => {
    const candidate = input.value.trim();
    if (candidate && !/^[A-Za-z0-9_-]{43}$/.test(candidate)) {
      key = "";
      setKeyError("Enter a generated 43-character MockAPI key.");
    } else {
      key = candidate;
      setKeyError("");
    }
    setControls();
  });
  events.listen(generate, "click", () => update(true));
  events.listen(apply, "click", () => update(false));
  events.listen(copy, "click", () => copyToClipboard(key, "API key copied"));
  setControls();

  function close() {
    generation += 1;
    input.value = "";
    setKeyError("");
  }

  return {
    open,
    close,
    getKey: () => key,
    dispose() {
      disposed = true;
      close();
      key = "";
      events.clear();
    },
  };
}
