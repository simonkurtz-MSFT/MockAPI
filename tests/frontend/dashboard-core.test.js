import { describe, expect, it } from "vitest";
import {
  explainEndpointStatistics,
  filterEndpoints,
  formatBytes,
  formatMergeResult,
  formatProblem,
  methodSupportsBody,
  parseHeaderLines,
} from "../../src/MockAPI/wwwroot/dashboard-core.js";

const endpoints = [
  {
    name: "Hello",
    path: "/ex/hello",
    enabled: true,
    methods: ["GET"],
    response: { statusCode: 200 },
  },
  {
    name: "Rate Limited",
    path: "/ex/rate-limited",
    enabled: false,
    methods: ["GET", "HEAD"],
    response: { statusCode: 429 },
  },
];

describe("filterEndpoints", () => {
  it.each([
    [{ query: "", method: "", enabled: "", statusClass: "" }, 2],
    [{ query: "hello", method: "", enabled: "", statusClass: "" }, 1],
    [{ query: "RATE-LIMITED", method: "", enabled: "", statusClass: "" }, 1],
    [{ query: "", method: "HEAD", enabled: "", statusClass: "" }, 1],
    [{ query: "", method: "", enabled: "false", statusClass: "" }, 1],
    [{ query: "", method: "", enabled: "", statusClass: "2" }, 1],
    [{ query: "hello", method: "POST", enabled: "", statusClass: "" }, 0],
  ])("applies filters %#", (filters, expectedCount) => {
    expect(filterEndpoints(endpoints, filters)).toHaveLength(expectedCount);
  });
});

describe("formatBytes", () => {
  it.each([
    [0, "0 B"],
    [1023, "1023 B"],
    [1024, "1.0 KiB"],
    [1024 * 1024, "1.0 MiB"],
  ])("formats %i bytes", (value, expected) => {
    expect(formatBytes(value)).toBe(expected);
  });
});

describe("formatMergeResult", () => {
  it("describes a no-op", () => {
    expect(formatMergeResult("example", { applied: false })).toBe("No example changes were needed");
  });

  it("describes every applied result category", () => {
    expect(formatMergeResult("example", { applied: true, added: 2, updated: 1, skipped: 3 })).toBe(
      "Examples: 2 added, 1 updated, 3 already present"
    );
  });

  it("uses the template label and omits zero categories", () => {
    expect(formatMergeResult("template", { applied: true, added: 1, updated: 0, skipped: 0 })).toBe(
      "Template: 1 added"
    );
    expect(formatMergeResult("template", { applied: true })).toBe("Template: ");
  });
});

describe("formatProblem", () => {
  it("formats validation errors", () => {
    const error = {
      message: "fallback",
      problem: { errors: [{ path: "$.path", message: "is reserved" }] },
    };
    expect(formatProblem(error)).toBe("$.path: is reserved");
  });

  it("uses the error message without validation errors", () => {
    expect(formatProblem(new Error("network failed"))).toBe("network failed");
  });
});

describe("parseHeaderLines", () => {
  it("parses repeated headers and values containing colons", () => {
    expect(parseHeaderLines("X-Test: one\nX-Test: two\nLocation: https://example.test/a\n")).toEqual([
      ["X-Test", "one"],
      ["X-Test", "two"],
      ["Location", "https://example.test/a"],
    ]);
  });

  it.each(["missing separator", ": missing name"])("rejects malformed line %s", (line) => {
    expect(() => parseHeaderLines(line)).toThrow("Request header line 1 must use Name: value.");
  });
});

describe("methodSupportsBody", () => {
  it.each([
    ["GET", false],
    ["HEAD", false],
    ["POST", true],
    ["PATCH", true],
  ])("returns %s support", (method, expected) => {
    expect(methodSupportsBody(method)).toBe(expected);
  });
});

describe("explainEndpointStatistics", () => {
  const endpoint = {
    response: { statusCode: 429, headers: { "Retry-After": ["30"] } },
  };

  it("explains a configured 429 without claiming a runtime rate-limit condition", () => {
    expect(explainEndpointStatistics(endpoint, { totalRequests: 6, lastStatusCode: 429 })).toBe(
      "Every matched request is configured to return HTTP 429. The configured Retry-After value is 30. No request-count or time-window rate-limit condition is configured."
    );
  });

  it("explains a configured 429 without a Retry-After header", () => {
    const endpointWithoutHeaders = { response: { statusCode: 429 } };
    expect(explainEndpointStatistics(endpointWithoutHeaders, { totalRequests: 1, lastStatusCode: 429 })).toBe(
      "Every matched request is configured to return HTTP 429. No request-count or time-window rate-limit condition is configured."
    );
  });

  it("explains ordinary configured responses as unconditional", () => {
    const successfulEndpoint = { response: { statusCode: 204 } };
    expect(explainEndpointStatistics(successfulEndpoint, { totalRequests: 3, lastStatusCode: 204 })).toBe(
      "Every matched request is configured to return HTTP 204. The activity graph shows when requests reached this endpoint, not a conditional response rule."
    );
  });

  it("identifies statistics collected before a configuration change", () => {
    expect(explainEndpointStatistics(endpoint, { totalRequests: 5, lastStatusCode: 200 })).toBe(
      "The last observed response was HTTP 200, but the endpoint is now configured for HTTP 429. These statistics include activity from before the latest configuration change."
    );
  });

  it("describes the configured response when no requests exist", () => {
    expect(explainEndpointStatistics(endpoint, null)).toBe(
      "No requests have been observed. This endpoint is configured to return HTTP 429 for every matched request."
    );
  });
});
