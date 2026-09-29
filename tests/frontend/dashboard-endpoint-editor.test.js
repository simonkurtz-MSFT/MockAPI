import { describe, expect, it, vi } from "vitest";
import {
  buildEndpointDefinition,
  createEndpointEditorValues,
  getResponseEditorState,
  HTTP_STATUS_REASONS,
} from "../../src/MockAPI/wwwroot/dashboard-endpoint-editor.js";

describe("createEndpointEditorValues", () => {
  it("provides readable defaults for a new endpoint", () => {
    expect(createEndpointEditorValues()).toMatchObject({
      id: null,
      name: "",
      path: "/",
      enabled: true,
      methods: ["GET"],
      behavior: "response",
      statusCode: 200,
      reasonPhrase: "OK",
      rateLimitEnabled: false,
    });
  });

  it("copies an existing response, headers, and rate limit into detached editor values", () => {
    const endpoint = {
      id: "endpoint-id",
      name: "Limited",
      description: "A limited response",
      enabled: false,
      methods: ["POST", "HEAD"],
      path: "/limited",
      requestCount: 5,
      response: {
        statusCode: 429,
        reasonPhrase: "Slow down",
        headers: { "Retry-After": ["10", "20"] },
        contentType: "application/json",
        body: "{}",
        rateLimit: {
          requestLimit: 5,
          windowSeconds: 30,
          successResponse: {
            statusCode: 202,
            headers: {},
            contentType: "text/plain",
            body: "accepted",
          },
        },
      },
    };

    const values = createEndpointEditorValues(endpoint);

    expect(values).toMatchObject({
      id: "endpoint-id",
      methods: ["POST", "HEAD"],
      requestCount: 5,
      headers: [{ name: "Retry-After", values: ["10", "20"] }],
      rateLimitEnabled: true,
      requestLimit: 5,
      windowSeconds: 30,
      successStatusCode: 202,
    });
    values.methods.push("DELETE");
    values.headers[0].values.push("30");
    expect(endpoint.methods).toEqual(["POST", "HEAD"]);
    expect(endpoint.response.headers["Retry-After"]).toEqual(["10", "20"]);
  });

  it("uses an empty reason phrase when an existing response omits it", () => {
    const values = createEndpointEditorValues({
      id: "endpoint-id",
      name: "No reason phrase",
      enabled: true,
      methods: ["GET"],
      path: "/no-reason",
      response: { statusCode: 204, headers: {}, body: "" },
    });

    expect(values.reasonPhrase).toBe("");
  });
});

describe("buildEndpointDefinition", () => {
  it("normalizes editable text and preserves repeated response headers", () => {
    const createId = vi.fn(() => "new-id");
    const values = {
      ...createEndpointEditorValues(),
      name: "  Greeting  ",
      description: "Description",
      path: "  /hello  ",
      methods: ["GET", "HEAD"],
      headers: [
        { name: " X-Test ", values: [" first ", "", "second"] },
        { name: "   ", values: ["ignored"] },
      ],
      body: "hello",
    };

    expect(buildEndpointDefinition(values, createId)).toEqual({
      id: "new-id",
      name: "Greeting",
      description: "Description",
      enabled: true,
      methods: ["GET", "HEAD"],
      path: "/hello",
      response: {
        statusCode: 200,
        reasonPhrase: "OK",
        headers: { "X-Test": ["first", "second"] },
        contentType: "application/json; charset=utf-8",
        body: "hello",
      },
    });
    expect(createId).toHaveBeenCalledOnce();
  });

  it("adds a leading slash to a path before saving", () => {
    const values = {
      ...createEndpointEditorValues(),
      path: "  hello  ",
    };

    expect(buildEndpointDefinition(values, () => "new-id").path).toBe("/hello");
  });

  it("uses the stable ID and includes the configured permitted-request response", () => {
    const createId = vi.fn();
    const values = {
      ...createEndpointEditorValues(),
      id: "stable-id",
      requestCount: 5,
      rateLimitEnabled: true,
      requestLimit: 3,
      windowSeconds: 45,
      successStatusCode: 204,
      successContentType: "",
      successBody: "",
    };

    expect(buildEndpointDefinition(values, createId)).toMatchObject({
      id: "stable-id",
      requestCount: 5,
      response: {
        rateLimit: {
          requestLimit: 3,
          windowSeconds: 45,
          successResponse: { statusCode: 204, headers: {}, contentType: null, body: "" },
        },
      },
    });
    expect(createId).not.toHaveBeenCalled();
  });

  it("normalizes whitespace-only optional response text to null", () => {
    const values = {
      ...createEndpointEditorValues(),
      reasonPhrase: "   ",
      contentType: "   ",
    };

    expect(buildEndpointDefinition(values, () => "new-id").response).toMatchObject({
      reasonPhrase: null,
      contentType: null,
    });
  });

  it("clears response-only values for an intentional connection abort", () => {
    const values = {
      ...createEndpointEditorValues(),
      behavior: "abortConnection",
      statusCode: 503,
      reasonPhrase: "Unavailable",
      contentType: "text/plain",
      body: "must not leak",
      headers: [{ name: "X-Test", values: ["must not leak"] }],
      rateLimitEnabled: true,
    };

    expect(buildEndpointDefinition(values, () => "abort-id").response).toEqual({
      behavior: "abortConnection",
      reasonPhrase: null,
      headers: {},
      contentType: null,
      body: "",
    });
  });
});

describe("getResponseEditorState", () => {
  it.each([
    ["response", false, { responseDisabled: false, rateLimitVisible: false }],
    ["response", true, { responseDisabled: false, rateLimitVisible: true }],
    ["abortConnection", true, { responseDisabled: true, rateLimitVisible: false }],
  ])("derives controls for %s with rate limit %s", (behavior, enabled, expected) => {
    expect(getResponseEditorState(behavior, enabled)).toEqual(expected);
  });
});

describe("HTTP_STATUS_REASONS", () => {
  it("provides the standard editor reason phrase lookup", () => {
    expect(HTTP_STATUS_REASONS.get("418")).toBe("I'm a Teapot");
  });
});
