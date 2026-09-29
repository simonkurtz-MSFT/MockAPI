import { describe, expect, it, vi } from "vitest";
import {
  createDashboardCommandRunner,
  createDashboardManagementClient,
} from "../../src/MockAPI/wwwroot/dashboard-management.js";

function deferred() {
  let resolve;
  const promise = new Promise((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

describe("dashboard management transport", () => {
  it("reads non-storable JSON and forwards cancellation and custom headers", async () => {
    const request = vi.fn().mockResolvedValue(Response.json({ revision: 1 }));
    const api = createDashboardManagementClient({ request });
    const controller = new AbortController();

    await expect(
      api("/configuration", { signal: controller.signal, headers: { Accept: "application/json" } })
    ).resolves.toEqual({ revision: 1 });

    const [url, options] = request.mock.calls[0];
    expect(url).toBe("/__mockapi/api/configuration");
    expect(options.signal).toBe(controller.signal);
    expect(options.cache).toBe("no-store");
    expect(options.headers.get("Accept")).toBe("application/json");
    expect(options.headers.has("Content-Type")).toBe(false);
    expect(options.headers.has("If-Match")).toBe(false);
  });

  it("uses only the explicitly captured write revision and strips client-only options", async () => {
    const request = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const api = createDashboardManagementClient({ request });

    await expect(
      api("/endpoints", {
        method: "POST",
        body: "{}",
        mutatesConfiguration: true,
        etag: '"7"',
        cache: "force-cache",
      })
    ).resolves.toBeNull();

    const options = request.mock.calls[0][1];
    expect(options.headers.get("Content-Type")).toBe("application/json");
    expect(options.headers.get("If-Match")).toBe('"7"');
    expect(options.cache).toBe("no-store");
    expect(options).not.toHaveProperty("mutatesConfiguration");
    expect(options).not.toHaveProperty("etag");
    expect(options).not.toHaveProperty("expectedEtag");
  });

  it.each([undefined, null, "", 'W/"1"', "*", "1"])(
    "rejects a mutation with invalid revision %s before sending",
    async (etag) => {
      const request = vi.fn();
      const api = createDashboardManagementClient({ request });

      await expect(api("/configuration/save", { method: "POST", mutatesConfiguration: true, etag })).rejects.toThrow(
        "configuration revision"
      );
      expect(request).not.toHaveBeenCalled();
    }
  );

  it("accepts a matching endpoint-read revision", async () => {
    const request = vi.fn().mockResolvedValue(Response.json([], { headers: { ETag: '"3"' } }));
    const api = createDashboardManagementClient({ request });

    await expect(api("/endpoints", { expectedEtag: '"3"' })).resolves.toEqual([]);
  });

  it.each([null, '"4"'])("rejects a successful endpoint read with obsolete or absent ETag %s", async (etag) => {
    const response = Response.json([{ id: "newer-endpoint" }], { headers: etag ? { ETag: etag } : {} });
    const readBody = vi.spyOn(response, "json");
    const api = createDashboardManagementClient({ request: vi.fn().mockResolvedValue(response) });

    await expect(api("/endpoints", { expectedEtag: '"3"' })).rejects.toThrow("resynchronizing");
    expect(readBody).not.toHaveBeenCalled();
  });

  it("preserves problem details and status without retrying a rejected write", async () => {
    const problem = { detail: "The configuration changed.", conflicts: [{ builtInName: "Example" }] };
    const request = vi.fn().mockResolvedValue(Response.json(problem, { status: 409 }));
    const api = createDashboardManagementClient({ request });

    await expect(
      api("/configuration/example/merge", { method: "POST", mutatesConfiguration: true, etag: '"2"' })
    ).rejects.toMatchObject({ message: problem.detail, status: 409, problem });
    expect(request).toHaveBeenCalledTimes(1);
  });

  it.each(["not JSON", "null", '"error"', "{}"])(
    "reports HTTP status when problem details are unusable: %s",
    async (body) => {
      const request = vi.fn().mockResolvedValue(new Response(body, { status: 502, statusText: "Bad Gateway" }));
      const api = createDashboardManagementClient({ request });

      await expect(api("/endpoints", { expectedEtag: '"1"' })).rejects.toMatchObject({
        message: "502 Bad Gateway",
        status: 502,
      });
    }
  );

  it("surfaces malformed success payloads", async () => {
    const api = createDashboardManagementClient({ request: vi.fn().mockResolvedValue(new Response("not JSON")) });
    await expect(api("/configuration")).rejects.toBeInstanceOf(SyntaxError);
  });

  it("propagates request cancellation without replacing the error", async () => {
    const error = new DOMException("Cancelled", "AbortError");
    const api = createDashboardManagementClient({ request: vi.fn().mockRejectedValue(error) });
    await expect(api("/configuration")).rejects.toBe(error);
  });

  it("propagates cancellation while reading an error response", async () => {
    const error = new DOMException("Cancelled", "AbortError");
    const response = new Response(null, { status: 500 });
    vi.spyOn(response, "json").mockRejectedValue(error);
    const api = createDashboardManagementClient({ request: vi.fn().mockResolvedValue(response) });
    await expect(api("/configuration")).rejects.toBe(error);
  });
});

function commandHarness() {
  const synchronize = vi.fn().mockResolvedValue();
  const onError = vi.fn();
  const onPendingChange = vi.fn();
  const runner = createDashboardCommandRunner({ synchronize, onError, onPendingChange });
  return { runner, synchronize, onError, onPendingChange };
}

describe("dashboard management command lifecycle", () => {
  it("holds the single-flight guard through the operation and resynchronization", async () => {
    const h = commandHarness();
    const operation = deferred();
    const synchronization = deferred();
    h.synchronize.mockReturnValue(synchronization.promise);
    const action = vi.fn().mockReturnValue(operation.promise);
    const first = h.runner.run(action);

    expect(h.onPendingChange).toHaveBeenCalledWith(true);
    expect(await h.runner.run(action)).toEqual({ kind: "busy" });
    expect(h.onError).toHaveBeenCalledWith(
      expect.objectContaining({ message: expect.stringContaining("in progress") })
    );
    operation.resolve();
    await Promise.resolve();
    expect(h.synchronize).toHaveBeenCalledTimes(1);
    expect(await h.runner.run(action)).toEqual({ kind: "busy" });
    expect(action).toHaveBeenCalledTimes(1);
    synchronization.resolve();
    expect(await first).toEqual({ kind: "completed" });
    expect(h.onPendingChange.mock.calls).toEqual([[true], [false]]);
    expect(await h.runner.run(vi.fn())).toEqual({ kind: "completed" });
  });

  it("reports a failed command, refreshes authoritative state, and releases the guard", async () => {
    const h = commandHarness();
    const error = new Error("Stale revision");
    const action = vi.fn().mockRejectedValue(error);

    expect(await h.runner.run(action)).toEqual({ kind: "failed" });
    expect(h.onError).toHaveBeenCalledWith(error);
    expect(h.synchronize).toHaveBeenCalledTimes(1);
    expect(action).toHaveBeenCalledTimes(1);
    expect(h.onPendingChange.mock.calls).toEqual([[true], [false]]);
  });

  it("supports form or conflict-specific error presentation", async () => {
    const h = commandHarness();
    const present = vi.fn();
    const error = new Error("Invalid endpoint");

    expect(
      await h.runner.run(() => {
        throw error;
      }, present)
    ).toEqual({ kind: "failed" });
    expect(present).toHaveBeenCalledWith(error);
    expect(h.onError).not.toHaveBeenCalled();
  });

  it("reports refresh failure separately from the command failure", async () => {
    const h = commandHarness();
    const commandError = new Error("Mutation failed");
    const refreshError = new Error("Refresh failed");
    const present = vi.fn();
    h.synchronize.mockRejectedValue(refreshError);

    expect(
      await h.runner.run(() => {
        throw commandError;
      }, present)
    ).toEqual({ kind: "failed" });
    expect(present).toHaveBeenCalledWith(commandError);
    expect(h.onError).toHaveBeenCalledWith(refreshError);
    expect(h.onPendingChange).toHaveBeenLastCalledWith(false);
  });

  it("does not report completion when refreshing after a successful command fails", async () => {
    const h = commandHarness();
    h.synchronize.mockRejectedValue(new Error("Refresh failed"));

    expect(await h.runner.run(vi.fn())).toEqual({ kind: "failed" });
    expect(h.onError).toHaveBeenCalledTimes(1);
  });

  it("clears pending state even if an error presenter throws", async () => {
    const h = commandHarness();
    const error = new Error("Presenter failed");

    await expect(
      h.runner.run(
        () => {
          throw new Error("Failed");
        },
        () => {
          throw error;
        }
      )
    ).rejects.toBe(error);
    expect(h.onPendingChange).toHaveBeenLastCalledWith(false);
  });
});
