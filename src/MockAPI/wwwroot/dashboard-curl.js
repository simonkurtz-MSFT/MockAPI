"use strict";

import { createDefaultTestRequest } from "./dashboard-test-request.js?v={{ASSET_VERSION}}";

/**
 * Builds a clipboard-ready request using the first configured method and the test blade's illustrative payload.
 * Uses PowerShell on Windows and POSIX shell quoting elsewhere. Never accepts or embeds a secret.
 * @param {import("./dashboard-core.js").MockEndpoint} endpoint Configured operation.
 * @param {string} origin Current dashboard origin.
 * @param {string} platform Browser operating-system platform.
 * @param {boolean} requireApiKey Whether to reference the caller's MOCKAPI_KEY environment variable.
 * @returns {string} One shell command, without a trailing newline.
 * @throws {TypeError} The dashboard origin is not a valid absolute URL.
 */
export function createEndpointCurlCommand(endpoint, origin, platform, requireApiKey) {
  const windows = /^win/i.test(platform);
  // PowerShell also treats typographic apostrophes as string delimiters.
  /** @type {(value: string) => string} */
  const quote = windows
    ? (value) => `'${value.replace(/['\u2018\u2019]/g, "$&$&")}'`
    : (value) => `'${value.replaceAll("'", "'\"'\"'")}'`;
  const method = endpoint.methods[0];
  // Concatenation keeps even a path beginning with // on this instance, never a different host.
  const url = `${new URL(origin).origin}${endpoint.path}`;
  const parts = [windows ? "curl.exe" : "curl", "--globoff", "--fail-with-body"];
  if (method === "HEAD") parts.push("--head");
  else parts.push("--request", quote(method));
  parts.push("--url", quote(url));
  if (requireApiKey) {
    parts.push("--header", windows ? '"X-MockAPI-Key: ${env:MOCKAPI_KEY}"' : '"X-MockAPI-Key: ${MOCKAPI_KEY}"');
  }
  const sample = createDefaultTestRequest(method, endpoint.path);
  if (sample.body) {
    parts.push("--header", quote(sample.headerLines), "--data-raw", quote(JSON.stringify(JSON.parse(sample.body))));
  }
  return parts.join(" ");
}
