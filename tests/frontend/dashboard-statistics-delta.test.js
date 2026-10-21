import { describe, expect, it } from "vitest";
import { applyStatisticsDelta } from "../../src/MockAPI/wwwroot/dashboard-core.js";

const start = Date.parse("2026-01-01T00:00:00Z");
const bucket = (index, requests = 0) => ({
  minuteUtc: new Date(start + index * 60_000).toISOString(),
  requests,
  informationalResponses: 0,
  successResponses: requests,
  redirectionResponses: 0,
  clientErrorResponses: 0,
  serverErrorResponses: 0,
});
const history = () => Array.from({ length: 60 }, (_, index) => bucket(index));
const endpoint = (id) => ({
  endpointId: id,
  totalRequests: 0,
  lastStatusCode: null,
  lastRequestUtc: null,
  responseBytes: 0,
  recentMinutes: history(),
});
const baseline = () => ({
  totalRequests: 0,
  matchedRequests: 0,
  unmatchedRequests: 0,
  failedWrites: 0,
  abortedConnections: 0,
  informationalResponses: 0,
  successResponses: 0,
  redirectionResponses: 0,
  clientErrorResponses: 0,
  serverErrorResponses: 0,
  responseBytes: 0,
  recentMinutes: history(),
  endpoints: [endpoint("a"), endpoint("b")],
  recentRequests: [],
});
const delta = (changes = {}) => ({
  baseSequence: 1,
  sequence: 2,
  windowStartUtc: new Date(start).toISOString(),
  totals: { totalRequests: 1 },
  recentMinutes: [],
  endpoints: [],
  removedEndpointIds: [],
  recentRequests: null,
  ...changes,
});

describe("statistics interval state deltas", () => {
  it("applies absolute values once and preserves untouched records without mutating the baseline", () => {
    const previous = baseline();
    const patch = delta({
      recentMinutes: [bucket(59, 1)],
      endpoints: [{ ...endpoint("b"), totalRequests: 1, recentMinutes: [bucket(59, 1)] }],
      recentRequests: [{ outcome: "response", transportAttempts: 1 }],
    });
    const result = applyStatisticsDelta(previous, patch);
    expect(result.totalRequests).toBe(1);
    expect(result.recentMinutes[59].requests).toBe(1);
    expect(result.recentMinutes[0]).toBe(previous.recentMinutes[0]);
    expect(result.endpoints[0]).toBe(previous.endpoints[0]);
    expect(result.endpoints[1].recentMinutes[0]).toBe(previous.endpoints[1].recentMinutes[0]);
    expect(result.endpoints[1].recentMinutes[59].requests).toBe(1);
    expect(applyStatisticsDelta(result, patch)).toEqual(result);
    expect(previous.totalRequests).toBe(0);
    expect(previous.endpoints[1].recentMinutes[59].requests).toBe(0);
    expect(result.recentRequests).toBe(patch.recentRequests);
  });

  it("keeps all history and endpoint identities when only aggregate counters change", () => {
    const previous = baseline();
    const result = applyStatisticsDelta(previous, delta());
    expect(result.endpoints).toBe(previous.endpoints);
    expect(result.recentMinutes).toBe(previous.recentMinutes);
    expect(result.recentRequests).toBe(previous.recentRequests);
  });

  it("adds and removes endpoints, fills zero buckets, orders IDs, and replaces a cleared log", () => {
    const previous = baseline();
    previous.recentRequests = [{ transportAttempts: 1 }];
    const result = applyStatisticsDelta(
      previous,
      delta({
        endpoints: [{ ...endpoint("0"), totalRequests: 4, recentMinutes: [bucket(59, 4)] }],
        removedEndpointIds: ["a"],
        recentRequests: [],
      })
    );
    expect(result.endpoints.map((entry) => entry.endpointId)).toEqual(["0", "b"]);
    expect(result.endpoints[0].recentMinutes).toHaveLength(60);
    expect(result.endpoints[0].recentMinutes[0]).toEqual(bucket(0));
    expect(result.endpoints[0].recentMinutes[59]).toEqual(bucket(59, 4));
    expect(result.recentRequests).toEqual([]);
  });

  it("advances all histories, expires old minutes, and accepts equivalent UTC spellings", () => {
    const previous = baseline();
    previous.endpoints[0].recentMinutes[0] = bucket(0, 9);
    const result = applyStatisticsDelta(
      previous,
      delta({
        windowStartUtc: "2026-01-01T00:01:00+00:00",
        recentMinutes: [bucket(60, 2)],
        endpoints: [{ ...endpoint("b"), recentMinutes: [bucket(60, 2)] }],
      })
    );
    expect(result.recentMinutes[0]).toBe(previous.recentMinutes[1]);
    expect(result.recentMinutes[59]).toEqual(bucket(60, 2));
    expect(result.endpoints[0].recentMinutes.reduce((sum, entry) => sum + entry.requests, 0)).toBe(0);
    expect(result.endpoints[1].recentMinutes[59].requests).toBe(2);
    expect(previous.endpoints[0].recentMinutes[0].requests).toBe(9);
  });

  it("builds an initial window from an empty history and rejects invalid timestamps", () => {
    const previous = { ...baseline(), recentMinutes: [] };
    expect(applyStatisticsDelta(previous, delta()).recentMinutes).toHaveLength(60);
    expect(() => applyStatisticsDelta(baseline(), delta({ windowStartUtc: "invalid" }))).toThrow(
      "Invalid statistics retention window"
    );
  });
});
