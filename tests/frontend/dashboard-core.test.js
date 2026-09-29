import { describe, expect, it } from "vitest";
import {
  checkJsonBody,
  explainEndpointStatistics,
  filterEndpoints,
  formatBytes,
  formatJsonBody,
  formatMergeResult,
  formatNumber,
  formatProblem,
  formatRequestLogResult,
  formatTestResponseDetails,
  formatTime,
  formatTransportAttemptInsight,
  formatUtcTime,
  groupEndpointsByPath,
  groupRequestsByMinute,
  hasScrollableStatisticsHistory,
  methodSupportsBody,
  paginateItems,
  parseHeaderLines,
  sortEndpoints,
} from "../../src/MockAPI/wwwroot/dashboard-core.js";

describe("formatTransportAttemptInsight", () => {
  it("explains grouped browser retries and stays empty for single attempts", () => {
    expect(formatTransportAttemptInsight([{}])).toBe("");
    expect(formatTransportAttemptInsight([{ transportAttempts: 1 }])).toBe("");
    expect(formatTransportAttemptInsight([{ transportAttempts: 6 }, { transportAttempts: 1 }])).toBe(
      "1 dashboard test produced 6 transport attempts. Chrome may automatically retry an idempotent request, such as GET, when the server intentionally closes the connection before sending HTTP response headers. MockAPI counts every transport attempt in statistics and groups attempts from the same dashboard test in this log."
    );
    expect(formatTransportAttemptInsight([{ transportAttempts: 2 }, { transportAttempts: 3 }])).toContain(
      "2 dashboard tests produced 5 transport attempts."
    );
  });
});

const endpoints = [
  {
    name: "Hello",
    description: "Healthy greeting response",
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
    [{ query: "healthy greeting", method: "", enabled: "", statusClass: "" }, 1],
    [{ query: "", method: "HEAD", enabled: "", statusClass: "" }, 1],
    [{ query: "", method: "", enabled: "false", statusClass: "" }, 1],
    [{ query: "", method: "", enabled: "", statusClass: "2" }, 1],
    [{ query: "hello", method: "POST", enabled: "", statusClass: "" }, 0],
  ])("applies filters %#", (filters, expectedCount) => {
    expect(filterEndpoints(endpoints, filters)).toHaveLength(expectedCount);
  });

  it("does not classify an aborted connection as an HTTP status", () => {
    const abortEndpoint = {
      response: { behavior: "abortConnection" },
      methods: ["GET"],
      name: "Abort",
      path: "/abort",
    };
    expect(filterEndpoints([abortEndpoint], { query: "", method: "", enabled: "", statusClass: "5" })).toHaveLength(0);
  });
});

describe("sortEndpoints", () => {
  it("sorts text naturally without mutating the source", () => {
    const source = [
      { id: "ten", name: "Endpoint 10", methods: ["POST"], path: "/z", response: { statusCode: 204 } },
      { id: "two", name: "Endpoint 2", methods: ["GET"], path: "/a", response: { statusCode: 429 } },
    ];

    expect(sortEndpoints(source, { key: "name", direction: "ascending" }).map((endpoint) => endpoint.id)).toEqual([
      "two",
      "ten",
    ]);
    expect(source.map((endpoint) => endpoint.id)).toEqual(["ten", "two"]);
  });

  it("sorts response and live statistics columns", () => {
    const source = [
      { id: "drop", name: "Drop", methods: ["GET"], path: "/drop", response: { behavior: "abortConnection" } },
      { id: "ok", name: "OK", methods: ["GET"], path: "/ok", response: { statusCode: 200 } },
    ];
    const statistics = [
      { endpointId: "drop", totalRequests: 2, lastRequestUtc: "2026-01-01T00:00:00Z" },
      { endpointId: "ok", totalRequests: 10, lastRequestUtc: "2026-02-01T00:00:00Z" },
    ];

    expect(sortEndpoints(source, { key: "response", direction: "ascending" }, statistics)[0].id).toBe("ok");
    expect(sortEndpoints(source, { key: "requests", direction: "descending" }, statistics)[0].id).toBe("ok");
    expect(sortEndpoints(source, { key: "lastRequest", direction: "descending" }, statistics)[0].id).toBe("ok");
  });

  it.each([
    ["methods", "get"],
    ["path", "get"],
    ["enabled", "post"],
  ])("sorts the %s column", (key, expectedFirstId) => {
    const source = [
      {
        id: "post",
        name: "Post",
        methods: ["POST"],
        path: "/z",
        enabled: false,
        response: { statusCode: 201 },
      },
      {
        id: "get",
        name: "Get",
        methods: ["GET"],
        path: "/a",
        enabled: true,
        response: { statusCode: 200 },
      },
    ];

    expect(sortEndpoints(source, { key, direction: "ascending" })[0].id).toBe(expectedFirstId);
  });

  it("uses empty statistics values and preserves source order for ties", () => {
    const source = [
      { id: "first", name: "First", methods: ["GET"], path: "/first", response: { statusCode: 200 } },
      { id: "second", name: "Second", methods: ["GET"], path: "/second", response: { statusCode: 200 } },
    ];

    expect(sortEndpoints(source, { key: "requests", direction: "ascending" }).map(({ id }) => id)).toEqual([
      "first",
      "second",
    ]);
    expect(sortEndpoints(source, { key: "lastRequest", direction: "ascending" }).map(({ id }) => id)).toEqual([
      "first",
      "second",
    ]);
  });
});

describe("paginateItems", () => {
  it("returns page metadata and clamps an out-of-range page", () => {
    expect(paginateItems([1, 2, 3, 4, 5], 4, 2)).toEqual({
      items: [5],
      page: 3,
      pageCount: 3,
      start: 5,
      end: 5,
    });
  });

  it("describes an empty result set", () => {
    expect(paginateItems([], 1, 10)).toEqual({ items: [], page: 1, pageCount: 1, start: 0, end: 0 });
  });
});

describe("hasScrollableStatisticsHistory", () => {
  it("enables scrolling only when activity falls before the visible window", () => {
    const recentMinutes = Array.from({ length: 60 }, () => ({ requests: 0 }));

    expect(hasScrollableStatisticsHistory(recentMinutes, 30)).toBe(false);

    recentMinutes[59].requests = 1;
    expect(hasScrollableStatisticsHistory(recentMinutes, 30)).toBe(false);

    recentMinutes[29].requests = 1;
    expect(hasScrollableStatisticsHistory(recentMinutes, 30)).toBe(true);
  });
});

describe("groupEndpointsByPath", () => {
  it("groups endpoints by first path segment while preserving order", () => {
    const grouped = groupEndpointsByPath([
      { id: "one", path: "/inference-failover/200/chat/completions" },
      { id: "two", path: "/orders" },
      { id: "three", path: "/inference-failover/429/chat/completions" },
      { id: "root", path: "/" },
    ]);

    expect(grouped).toEqual([
      {
        key: "inference-failover",
        label: "inference-failover/",
        endpoints: [
          { id: "one", path: "/inference-failover/200/chat/completions" },
          { id: "three", path: "/inference-failover/429/chat/completions" },
        ],
      },
      { key: "orders", label: "orders/", endpoints: [{ id: "two", path: "/orders" }] },
      { key: "/", label: "/", endpoints: [{ id: "root", path: "/" }] },
    ]);
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

describe("dashboard display formatting", () => {
  it("formats numbers and timestamps using the active locale", () => {
    const timestamp = "2026-09-04T15:30:00Z";
    expect(formatNumber(1234567)).toBe(new Intl.NumberFormat().format(1234567));
    expect(formatTime(timestamp)).toBe(
      new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "short" }).format(new Date(timestamp))
    );
    expect(formatUtcTime(timestamp)).toBe(
      new Intl.DateTimeFormat(undefined, {
        dateStyle: "short",
        timeStyle: "short",
        timeZone: "UTC",
      }).format(new Date(timestamp))
    );
  });

  it("uses the timestamp empty-state label", () => {
    expect(formatTime(null)).toBe("Never");
    expect(formatUtcTime(null)).toBe("Never");
  });

  it("composes copied test-response details in visible panel order", () => {
    expect(
      formatTestResponseDetails({
        status: "200 OK",
        elapsed: "12.3 ms",
        url: "https://example.test/orders",
        headers: "content-type: application/json",
        body: '{"status":"ready"}',
      })
    ).toBe(
      'Status: 200 OK\nElapsed: 12.3 ms\nURL: https://example.test/orders\n\ncontent-type: application/json\n\n{"status":"ready"}'
    );
  });
});

describe("groupRequestsByMinute", () => {
  it("groups contiguous requests into UTC minute buckets while preserving newest-first order", () => {
    const requests = [
      { timestampUtc: "2026-09-04T15:30:59Z", path: "/newest" },
      { timestampUtc: "2026-09-04T15:30:01Z", path: "/same-minute" },
      { timestampUtc: "2026-09-04T15:29:59Z", path: "/older" },
    ];

    expect(groupRequestsByMinute(requests)).toEqual([
      {
        minuteUtc: "2026-09-04T15:30:00.000Z",
        requests: [requests[0], requests[1]],
      },
      {
        minuteUtc: "2026-09-04T15:29:00.000Z",
        requests: [requests[2]],
      },
    ]);
  });
});

describe("formatRequestLogResult", () => {
  it.each([
    [
      { outcome: "response", statusCode: 201 },
      { label: "201", className: "status-2xx" },
    ],
    [{ outcome: "response" }, { label: "Response", className: "response" }],
    [
      { outcome: "unmatched", statusCode: 404 },
      { label: "404 · Unmatched", className: "status-4xx" },
    ],
    [{ outcome: "unmatched" }, { label: "404 · Unmatched", className: "status-4xx" }],
    [
      { outcome: "failedWrite", statusCode: 503 },
      { label: "Write failed", className: "error" },
    ],
    [{ outcome: "aborted" }, { label: "Aborted", className: "error" }],
  ])("formats request outcome %#", (request, expected) => {
    expect(formatRequestLogResult(request)).toEqual(expected);
  });
});

describe("formatJsonBody", () => {
  it.each(["application/json", "application/json; charset=utf-8", "application/problem+json"])(
    "formats valid JSON for %s",
    (contentType) => {
      expect(formatJsonBody(contentType, '{"message":"ok","items":[1,2]}')).toBe(
        '{\n  "message": "ok",\n  "items": [\n    1,\n    2\n  ]\n}'
      );
    }
  );

  it("preserves invalid JSON and non-JSON bodies", () => {
    expect(formatJsonBody("application/json", '{"message":')).toBe('{"message":');
    expect(formatJsonBody("text/plain", '{"message":"ok"}')).toBe('{"message":"ok"}');
  });
});

describe("checkJsonBody", () => {
  it("distinguishes valid, malformed, empty, and non-JSON response bodies", () => {
    expect(checkJsonBody("application/problem+json", '{"message":"ok"}')).toEqual({
      kind: "valid",
      message: "Valid JSON. The response body has been formatted.",
    });
    expect(checkJsonBody("application/json", "  ")).toEqual({
      kind: "empty",
      message: "The JSON response body is empty.",
    });
    expect(checkJsonBody("text/plain", '{"message":"ok"}')).toEqual({
      kind: "notJson",
      message: "Set a JSON content type to check this response body.",
    });

    const invalid = checkJsonBody("application/json", '{"message":');
    expect(invalid.kind).toBe("invalid");
    expect(invalid.message).toContain("Invalid JSON:");
  });
});

describe("formatMergeResult", () => {
  it("describes an idempotent example load", () => {
    expect(formatMergeResult({ applied: false })).toBe("No example changes were needed");
  });

  it("describes every applied result category", () => {
    expect(formatMergeResult({ applied: true, added: 2, updated: 1, skipped: 3 })).toBe(
      "Examples: 2 added, 1 updated, 3 already present"
    );
  });

  it.each([
    [{ applied: true, added: 2 }, "Examples: 2 added"],
    [{ applied: true, updated: 1 }, "Examples: 1 updated"],
    [{ applied: true, skipped: 3 }, "Examples: 3 already present"],
  ])("omits unapplied result categories %#", (result, expected) => {
    expect(formatMergeResult(result)).toBe(expected);
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
    response: { statusCode: 429, headers: { "Retry-After": ["10"] } },
  };

  it("explains a configured 429 without claiming a runtime rate-limit condition", () => {
    expect(explainEndpointStatistics(endpoint, { totalRequests: 6, lastStatusCode: 429 })).toBe(
      "Every matched request is configured to return HTTP 429. The configured Retry-After value is 10. No request-count or time-window rate-limit condition is configured."
    );
  });

  it("explains a configured 429 without a Retry-After header", () => {
    const endpointWithoutHeaders = { response: { statusCode: 429 } };
    expect(explainEndpointStatistics(endpointWithoutHeaders, { totalRequests: 1, lastStatusCode: 429 })).toBe(
      "Every matched request is configured to return HTTP 429. No request-count or time-window rate-limit condition is configured."
    );
  });

  it("explains a configured request-count and time-window rate limit", () => {
    const rateLimitedEndpoint = {
      response: {
        statusCode: 429,
        rateLimit: {
          requestLimit: 3,
          windowSeconds: 10,
          successResponse: { statusCode: 200 },
        },
      },
    };

    expect(explainEndpointStatistics(rateLimitedEndpoint, null)).toBe(
      "No attempts have been observed. The first 3 requests within 10 seconds return HTTP 200; later requests return HTTP 429."
    );
    expect(explainEndpointStatistics(rateLimitedEndpoint, { totalRequests: 4, lastStatusCode: 429 })).toBe(
      "The first 3 requests within 10 seconds return HTTP 200; later requests return HTTP 429. The last observed response was HTTP 429."
    );
  });

  it("explains ordinary configured responses as unconditional", () => {
    const successfulEndpoint = { response: { statusCode: 204 } };
    expect(explainEndpointStatistics(successfulEndpoint, { totalRequests: 3, lastStatusCode: 204 })).toBe(
      "Every matched request is configured to return HTTP 204. The activity graph shows when attempts reached this endpoint, not a conditional response rule."
    );
  });

  it("identifies statistics collected before a configuration change", () => {
    expect(explainEndpointStatistics(endpoint, { totalRequests: 5, lastStatusCode: 200 })).toBe(
      "The last observed response was HTTP 200, but the endpoint is now configured for HTTP 429. These statistics include activity from before the latest configuration change."
    );
  });

  it("describes the configured response when no requests exist", () => {
    expect(explainEndpointStatistics(endpoint, null)).toBe(
      "No attempts have been observed. This endpoint is configured to return HTTP 429 for every matched request."
    );
  });

  it("explains an intentional connection abort without claiming a status", () => {
    const abortEndpoint = { response: { behavior: "abortConnection" } };
    expect(explainEndpointStatistics(abortEndpoint, { totalRequests: 1, lastStatusCode: null })).toBe(
      "Every matched request intentionally aborts the connection before an HTTP response is sent."
    );
    expect(explainEndpointStatistics(abortEndpoint, null)).toBe(
      "No attempts have been observed. This endpoint is configured to abort the connection without an HTTP response."
    );
  });
});
