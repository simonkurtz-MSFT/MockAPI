import { describe, expect, it, vi } from "vitest";
import {
  createEndpointTestRequestController,
  formatResponseHeaders,
} from "../../src/MockAPI/wwwroot/dashboard-test-request.js";

const origin = "https://mockapi.test";

function createResponse({ status = 200, statusText = "OK", url = `${origin}/test`, headers = [], body = "" } = {}) {
  return {
    status,
    statusText,
    url,
    headers: new Headers(headers),
    text: vi.fn().mockResolvedValue(body),
  };
}

function createController(request, times = [10, 15]) {
  const now = vi.fn();
  for (const time of times) now.mockReturnValueOnce(time);
  return createEndpointTestRequestController({ origin, request, now, createRequestId: () => "test-request-id" });
}

describe("createEndpointTestRequestController", () => {
  it("injects the current memory-only API key and blocks credential-bearing redirects", async () => {
    const request = vi.fn().mockResolvedValue(createResponse());
    let key = "first-key";
    const controller = createEndpointTestRequestController({ origin, request, now: () => 0, getApiKey: () => key });
    await controller.send({ method: "GET", path: "/test", headerLines: "", body: "" });
    expect(request.mock.calls[0][1].headers.get("X-MockAPI-Key")).toBe("first-key");
    expect(request.mock.calls[0][1].redirect).toBe("error");
    key = "rotated-key";
    await controller.send({ method: "GET", path: "/test", headerLines: "", body: "" });
    expect(request.mock.calls[1][1].headers.get("X-MockAPI-Key")).toBe("rotated-key");
    await controller.send({
      method: "GET",
      path: "/test",
      headerLines: "X-MockAPI-Key: deliberate-invalid-key",
      body: "",
    });
    expect(request.mock.calls[2][1].headers.get("X-MockAPI-Key")).toBe("deliberate-invalid-key");
    await controller.send({ method: "GET", path: "https://untrusted.test/test", headerLines: "", body: "" });
    expect(request).toHaveBeenCalledTimes(3);
  });

  it("sends a bodyless request and returns a structured non-2xx response", async () => {
    const request = vi.fn().mockResolvedValue(
      createResponse({
        status: 429,
        statusText: "Too Many Requests",
        url: `${origin}/limited?attempt=2`,
        headers: [
          ["retry-after", "10"],
          ["x-source", "MockAPI"],
        ],
        body: '{"error":"later"}',
      })
    );
    const controller = createController(request, [20, 27.25]);

    const result = await controller.send({
      method: "GET",
      path: "/limited?attempt=2",
      headerLines: "X-Trace: one\nX-Trace: two",
      body: "ignored",
    });

    expect(result).toEqual({
      kind: "completed",
      status: 429,
      statusText: "Too Many Requests",
      url: `${origin}/limited?attempt=2`,
      elapsedMilliseconds: 7.25,
      headers: [
        ["retry-after", "10"],
        ["x-source", "MockAPI"],
      ],
      body: '{"error":"later"}',
    });
    const [url, options] = request.mock.calls[0];
    expect(url.href).toBe(`${origin}/limited?attempt=2`);
    expect(options).not.toHaveProperty("body");
    expect(options.headers.get("x-trace")).toBe("one, two");
    expect(options.headers.get("x-mockapi-dashboard-request-id")).toBe("test-request-id");
  });

  it("includes a request body for methods that support one", async () => {
    const request = vi.fn().mockResolvedValue(createResponse({ status: 201, statusText: "Created" }));
    const controller = createController(request);

    await controller.send({ method: "POST", path: "/test", headerLines: "", body: '{"id":42}' });

    expect(request.mock.calls[0][1].body).toBe('{"id":42}');
  });

  it("creates a logical request ID when no factory is supplied", async () => {
    const request = vi.fn().mockResolvedValue(createResponse());
    const now = vi.fn().mockReturnValue(10);

    try {
      for (const crypto of [{ randomUUID: () => "generated-id" }, {}, undefined]) {
        vi.stubGlobal("crypto", crypto);
        const controller = createEndpointTestRequestController({ origin, request, now });

        await controller.send({ method: "GET", path: "/test", headerLines: "", body: "" });

        const requestId = request.mock.lastCall[1].headers.get("x-mockapi-dashboard-request-id");
        expect(requestId).toBeTruthy();
      }
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("rejects invalid requests before fetch", async () => {
    const request = vi.fn();
    const controller = createController(request);

    await expect(
      controller.send({ method: "GET", path: "https://other.test/path", headerLines: "", body: "" })
    ).resolves.toEqual({
      kind: "validationError",
      message: "Endpoint tests must target the current MockAPI origin.",
    });
    await expect(controller.send({ method: "GET", path: "http://[", headerLines: "", body: "" })).resolves.toEqual(
      expect.objectContaining({ kind: "validationError" })
    );
    await expect(controller.send({ method: "GET", path: "/test", headerLines: "invalid", body: "" })).resolves.toEqual({
      kind: "validationError",
      message: "Request header line 1 must use Name: value.",
    });
    expect(request).not.toHaveBeenCalled();
  });

  it("returns network errors for Error and non-Error failures", async () => {
    const errorController = createController(vi.fn().mockRejectedValue(new Error("offline")));
    const valueController = createController(vi.fn().mockRejectedValue("disconnected"));

    await expect(errorController.send({ method: "GET", path: "/test", headerLines: "", body: "" })).resolves.toEqual({
      kind: "networkError",
      message: "offline",
    });
    await expect(valueController.send({ method: "GET", path: "/test", headerLines: "", body: "" })).resolves.toEqual({
      kind: "networkError",
      message: "disconnected",
    });
  });

  it("owns cancellation and permits another request after cleanup", async () => {
    const request = vi.fn(
      (_, { signal }) =>
        new Promise((_, reject) => {
          signal.addEventListener("abort", () => {
            const error = new Error("cancelled");
            error.name = "AbortError";
            reject(error);
          });
        })
    );
    const controller = createController(request);
    controller.cancel();
    const pending = controller.send({ method: "GET", path: "/test", headerLines: "", body: "" });

    controller.cancel();
    await expect(pending).resolves.toEqual({ kind: "cancelled" });
    request.mockResolvedValueOnce(createResponse());
    await expect(controller.send({ method: "GET", path: "/test", headerLines: "", body: "" })).resolves.toEqual(
      expect.objectContaining({ kind: "completed" })
    );
  });

  it("rejects overlapping sends without replacing the active request", async () => {
    let resolveRequest;
    const request = vi.fn(
      () =>
        new Promise((resolve) => {
          resolveRequest = resolve;
        })
    );
    const controller = createController(request);
    const first = controller.send({ method: "GET", path: "/first", headerLines: "", body: "" });

    await expect(controller.send({ method: "GET", path: "/second", headerLines: "", body: "" })).resolves.toEqual({
      kind: "busy",
      message: "A request is already in progress.",
    });
    resolveRequest(createResponse());
    await expect(first).resolves.toEqual(expect.objectContaining({ kind: "completed" }));
    expect(request).toHaveBeenCalledOnce();
  });
});

describe("formatResponseHeaders", () => {
  it("aligns header values and handles an empty response", () => {
    expect(
      formatResponseHeaders([
        ["a", "one"],
        ["longer", "two"],
      ])
    ).toBe("a      : one\nlonger : two");
    expect(formatResponseHeaders([])).toBe("");
  });
});
