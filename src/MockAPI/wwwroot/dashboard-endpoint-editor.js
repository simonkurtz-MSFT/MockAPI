"use strict";

/** Standard HTTP methods offered by the endpoint editor. @type {readonly string[]} */
export const ENDPOINT_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

/** Standard HTTP statuses offered by the endpoint editor. @type {ReadonlyArray<readonly [number, string]>} */
export const HTTP_STATUSES = [
  [100, "Continue"],
  [101, "Switching Protocols"],
  [102, "Processing"],
  [103, "Early Hints"],
  [200, "OK"],
  [201, "Created"],
  [202, "Accepted"],
  [203, "Non-Authoritative Information"],
  [204, "No Content"],
  [205, "Reset Content"],
  [206, "Partial Content"],
  [207, "Multi-Status"],
  [208, "Already Reported"],
  [226, "IM Used"],
  [300, "Multiple Choices"],
  [301, "Moved Permanently"],
  [302, "Found"],
  [303, "See Other"],
  [304, "Not Modified"],
  [305, "Use Proxy"],
  [307, "Temporary Redirect"],
  [308, "Permanent Redirect"],
  [400, "Bad Request"],
  [401, "Unauthorized"],
  [402, "Payment Required"],
  [403, "Forbidden"],
  [404, "Not Found"],
  [405, "Method Not Allowed"],
  [406, "Not Acceptable"],
  [407, "Proxy Authentication Required"],
  [408, "Request Timeout"],
  [409, "Conflict"],
  [410, "Gone"],
  [411, "Length Required"],
  [412, "Precondition Failed"],
  [413, "Content Too Large"],
  [414, "URI Too Long"],
  [415, "Unsupported Media Type"],
  [416, "Range Not Satisfiable"],
  [417, "Expectation Failed"],
  [418, "I'm a Teapot"],
  [421, "Misdirected Request"],
  [422, "Unprocessable Content"],
  [423, "Locked"],
  [424, "Failed Dependency"],
  [425, "Too Early"],
  [426, "Upgrade Required"],
  [428, "Precondition Required"],
  [429, "Too Many Requests"],
  [431, "Request Header Fields Too Large"],
  [451, "Unavailable For Legal Reasons"],
  [500, "Internal Server Error"],
  [501, "Not Implemented"],
  [502, "Bad Gateway"],
  [503, "Service Unavailable"],
  [504, "Gateway Timeout"],
  [505, "HTTP Version Not Supported"],
  [506, "Variant Also Negotiates"],
  [507, "Insufficient Storage"],
  [508, "Loop Detected"],
  [510, "Not Extended"],
  [511, "Network Authentication Required"],
];

/** Maps standard status codes to the reason phrases shown by the editor. @type {ReadonlyMap<string, string>} */
export const HTTP_STATUS_REASONS = new Map(HTTP_STATUSES.map(([code, reason]) => [String(code), reason]));

/** @typedef {Omit<import("./dashboard-core.js").MockEndpoint, "id"> & {id: string|null}} EndpointDraft */

/**
 * @typedef {object} EndpointEditorValues
 * @property {string|null} id Stable ID when editing, or null when creating.
 * @property {string} name Endpoint display name.
 * @property {string} description Optional endpoint description.
 * @property {string} path Exact endpoint path.
 * @property {boolean} enabled Whether the endpoint participates in matching.
 * @property {string[]} methods Selected HTTP methods.
 * @property {number|null} requestCount Optional request count preserved while editing.
 * @property {"response"|"abortConnection"} behavior Response behavior.
 * @property {number} statusCode Terminal response status.
 * @property {string} reasonPhrase Optional HTTP/1.x reason phrase.
 * @property {string} contentType Optional terminal response media type.
 * @property {string} body Raw terminal response body.
 * @property {{name: string, values: string[]}[]} headers Ordered editor header rows.
 * @property {boolean} rateLimitEnabled Whether rate-limit fields should be serialized.
 * @property {number} requestLimit Permits in one sliding window.
 * @property {number} windowSeconds Sliding-window duration.
 * @property {number} successStatusCode Permitted-request response status.
 * @property {string} successContentType Permitted-request response media type.
 * @property {string} successBody Raw permitted-request response body.
 */

/**
 * Creates the values displayed when opening the endpoint editor.
 *
 * @param {EndpointDraft|null} endpoint Existing endpoint, detached duplicate, or null for defaults.
 * @returns {EndpointEditorValues} Detached values safe to modify in form controls.
 */
export function createEndpointEditorValues(endpoint = null) {
  const response = endpoint?.response;
  const rateLimit = response?.rateLimit;

  return {
    id: endpoint?.id || null,
    name: endpoint?.name || "",
    description: endpoint?.description || "",
    path: endpoint?.path || "/",
    enabled: endpoint?.enabled ?? true,
    methods: endpoint ? [...endpoint.methods] : ["GET"],
    requestCount: endpoint?.requestCount ?? null,
    behavior: response?.behavior || "response",
    statusCode: response?.statusCode || 200,
    reasonPhrase: endpoint ? response?.reasonPhrase || "" : HTTP_STATUS_REASONS.get("200"),
    contentType: response?.contentType || "application/json; charset=utf-8",
    body: response?.body || "",
    headers: Object.entries(response?.headers || {}).map(([name, values]) => ({ name, values: [...values] })),
    rateLimitEnabled: Boolean(rateLimit),
    requestLimit: rateLimit?.requestLimit || 10,
    windowSeconds: rateLimit?.windowSeconds || 60,
    successStatusCode: rateLimit?.successResponse.statusCode || 200,
    successContentType: rateLimit?.successResponse.contentType || "application/json; charset=utf-8",
    successBody: rateLimit?.successResponse.body || "",
  };
}

/**
 * Builds the management API endpoint definition represented by editor values.
 * Abort behavior deliberately clears response-only fields so stale hidden inputs cannot leak into the contract.
 *
 * @param {EndpointEditorValues} values Current editor values.
 * @param {() => string} createId Stable-ID factory used only for new endpoints.
 * @returns {import("./dashboard-core.js").MockEndpoint} Endpoint definition for create or replace.
 */
export function buildEndpointDefinition(values, createId) {
  const abortsConnection = values.behavior === "abortConnection";
  /** @type {Record<string, string[]>} */
  const headers = {};
  const trimmedPath = values.path.trim();
  const path = trimmedPath.startsWith("/") ? trimmedPath : `/${trimmedPath}`;

  if (!abortsConnection) {
    for (const header of values.headers) {
      const name = header.name.trim();
      if (!name) continue;
      headers[name] = header.values.map((value) => value.trim()).filter(Boolean);
    }
  }

  /** @type {import("./dashboard-core.js").MockResponse} */
  const response = {
    ...(abortsConnection ? { behavior: "abortConnection" } : { statusCode: values.statusCode }),
    reasonPhrase: abortsConnection ? null : values.reasonPhrase.trim() || null,
    headers,
    contentType: abortsConnection ? null : values.contentType.trim() || null,
    body: abortsConnection ? "" : values.body,
  };

  if (!abortsConnection && values.rateLimitEnabled) {
    response.rateLimit = {
      requestLimit: values.requestLimit,
      windowSeconds: values.windowSeconds,
      successResponse: {
        statusCode: values.successStatusCode,
        headers: {},
        contentType: values.successContentType.trim() || null,
        body: values.successBody,
      },
    };
  }

  return {
    id: values.id || createId(),
    name: values.name.trim(),
    description: values.description || null,
    enabled: values.enabled,
    methods: [...values.methods],
    path,
    ...(values.requestCount === null ? {} : { requestCount: values.requestCount }),
    response,
  };
}

/**
 * Describes which response controls are available for the selected behavior.
 *
 * @param {"response"|"abortConnection"} behavior Selected endpoint behavior.
 * @param {boolean} rateLimitEnabled Whether rate limiting is selected.
 * @returns {{responseDisabled: boolean, rateLimitVisible: boolean}} Derived control state.
 */
export function getResponseEditorState(behavior, rateLimitEnabled) {
  const responseDisabled = behavior === "abortConnection";
  return {
    responseDisabled,
    rateLimitVisible: !responseDisabled && rateLimitEnabled,
  };
}
