"use strict";

import { methodSupportsBody, parseHeaderLines } from "./dashboard-core.js?v={{ASSET_VERSION}}";

/**
 * Explains missing credentials without interpreting configured mock 401/403 responses as authentication failures.
 * Explicit headers take precedence over the memory-only Settings key, including an explicitly empty header.
 * @param {import("./dashboard-core.js").ApiSecurityStatus} security Current server protection status.
 * @param {string} headerLines Newline-delimited request headers.
 * @param {string} apiKey Memory-only Settings key.
 * @returns {string} Actionable warning, or an empty string when no missing-key problem is known.
 * @throws {Error} Request headers are malformed.
 */
export function getEndpointTestSecurityWarning(security, headerLines, apiKey) {
  if (!security.enabled) return "";
  if (!security.configured)
    return "API-key protection is enabled, but no key has been generated. Requests will be rejected before the endpoint runs. Generate a key in Settings > Mock API security, or disable Require X-MockAPI-Key on mock requests there.";
  const headers = createTestHeaders(headerLines, apiKey);
  const suppliedKey = headers.get("X-MockAPI-Key");
  if (suppliedKey?.trim()) return "";
  return [
    "X-MockAPI-Key is presently required, but this test will not send a key. Requests will be rejected before the endpoint runs. Please take one of the following actions:",
    "",
    "- Add X-MockAPI-Key to Request headers, or",
    "- enter the existing key in Settings > Dashboard test key, or",
    "- disable Require X-MockAPI-Key on mock requests in Settings.",
  ].join("\n");
}

/**
 * @param {string} headerLines Request headers entered by the user.
 * @param {string} apiKey Memory-only Settings key.
 * @returns {Headers} Headers with the same credential precedence for guidance and sending.
 */
function createTestHeaders(headerLines, apiKey) {
  const headers = new Headers();
  for (const [name, value] of parseHeaderLines(headerLines)) headers.append(name, value);
  if (apiKey && !headers.has("X-MockAPI-Key")) headers.set("X-MockAPI-Key", apiKey);
  return headers;
}

/**
 * @typedef {Object} EndpointTestRequest
 * @property {string} method HTTP method.
 * @property {string} path Same-origin endpoint path or URL.
 * @property {string} headerLines Newline-delimited request headers.
 * @property {string} body Request body text.
 */

/**
 * @typedef {{kind: "busy", message: string}|
 * {kind: "validationError", message: string}|
 * {kind: "cancelled"}|
 * {kind: "networkError", message: string}|
 * {kind: "completed", status: number, statusText: string, url: string, elapsedMilliseconds: number, headers: [string, string][], body: string}} EndpointTestResult
 */

/**
 * @typedef {Object} EndpointTestRequestController
 * @property {(request: EndpointTestRequest) => Promise<EndpointTestResult>} send Sends one request when the controller is idle.
 * @property {() => void} cancel Aborts the active request, if any.
 */

/**
 * Creates a single-flight, same-origin endpoint test controller.
 *
 * @param {Object} options Controller dependencies.
 * @param {string} options.origin Allowed URL origin.
 * @param {(url: URL, options: RequestInit) => Promise<Response>} options.request Request implementation.
 * @param {() => number} options.now Monotonic clock used for elapsed time.
 * @param {() => AbortController} [options.createAbortController] Abort controller factory.
 * @param {() => string} [options.createRequestId] Logical request ID factory used to coalesce browser transport retries.
 * @param {() => string} [options.getApiKey] Memory-only key provider; explicit test headers take precedence.
 * @returns {EndpointTestRequestController} Test request controller.
 */
export function createEndpointTestRequestController({
  origin,
  request,
  now,
  createAbortController = () => new AbortController(),
  createRequestId = () => globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`,
  getApiKey = () => "",
}) {
  let activeController = null;

  function cancel() {
    activeController?.abort();
  }

  /** @type {EndpointTestRequestController["send"]} */
  async function send({ method, path, headerLines, body }) {
    if (activeController) {
      return { kind: "busy", message: "A request is already in progress." };
    }

    let url;
    try {
      url = new URL(path, origin);
    } catch (error) {
      return { kind: "validationError", message: `Invalid endpoint URL: ${error.message}` };
    }

    if (url.origin !== origin) {
      return {
        kind: "validationError",
        message: "Endpoint tests must target the current MockAPI origin.",
      };
    }

    let headers;
    try {
      headers = createTestHeaders(headerLines, getApiKey());
    } catch (error) {
      return { kind: "validationError", message: error.message };
    }
    headers.set("X-MockAPI-Dashboard-Request-Id", createRequestId());

    const controller = createAbortController();
    activeController = controller;
    const started = now();
    try {
      /** @type {RequestInit} */
      // Never forward a mock credential to a configured redirect target.
      const options = {
        method,
        headers,
        signal: controller.signal,
        redirect: headers.has("X-MockAPI-Key") ? "error" : "follow",
      };
      if (methodSupportsBody(method)) options.body = body;
      const response = await request(url, options);
      const responseBody = await response.text();
      return {
        kind: "completed",
        status: response.status,
        statusText: response.statusText,
        url: response.url,
        elapsedMilliseconds: now() - started,
        headers: [...response.headers.entries()],
        body: responseBody,
      };
    } catch (error) {
      if (error?.name === "AbortError") return { kind: "cancelled" };
      return { kind: "networkError", message: error?.message ?? String(error) };
    } finally {
      activeController = null;
    }
  }

  return { send, cancel };
}

/**
 * Aligns response headers for the dashboard's plain-text inspector.
 *
 * @param {[string, string][]} entries Response header entries.
 * @returns {string} Newline-delimited aligned headers.
 */
export function formatResponseHeaders(entries) {
  const longestName = Math.max(0, ...entries.map(([name]) => name.length));
  return entries.map(([name, value]) => `${name.padEnd(longestName)} : ${value}`).join("\n");
}
