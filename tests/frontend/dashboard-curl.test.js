import { describe, expect, it } from "vitest";
import { createEndpointCurlCommand } from "../../src/MockAPI/wwwroot/dashboard-curl.js";

const endpoint = { methods: ["GET", "POST"], path: "/inventory/items" };

describe("endpoint cURL commands", () => {
  it.each(["MacIntel", "Darwin", "Linux x86_64", ""])(
    "uses POSIX syntax on %s and only the first method",
    (platform) => {
      expect(createEndpointCurlCommand(endpoint, "https://mock.example", platform, false)).toBe(
        "curl --globoff --fail-with-body --request 'GET' --url 'https://mock.example/inventory/items'"
      );
    }
  );

  it("uses curl.exe instead of the PowerShell alias on Windows", () => {
    expect(createEndpointCurlCommand(endpoint, "https://mock.example", "Win32", true)).toBe(
      "curl.exe --globoff --fail-with-body --request 'GET' --url 'https://mock.example/inventory/items' --header \"X-MockAPI-Key: ${env:MOCKAPI_KEY}\""
    );
  });

  it("references a caller-supplied key in POSIX shells", () => {
    const command = createEndpointCurlCommand(endpoint, "https://mock.example", "Linux", true);
    expect(command).toContain('--header "X-MockAPI-Key: ${MOCKAPI_KEY}"');
  });

  it.each(["Win32", "MacIntel"])("uses HEAD semantics without a payload on %s", (platform) => {
    const command = createEndpointCurlCommand(
      { ...endpoint, methods: ["HEAD"] },
      "http://localhost:5080",
      platform,
      false
    );
    expect(command).toContain("--head --url");
    expect(command).not.toContain("--request");
    expect(command).not.toContain("--data");
  });

  it.each(["POST", "PUT", "PATCH"])("includes the shared illustrative JSON payload for %s", (method) => {
    const command = createEndpointCurlCommand(
      { methods: [method], path: "/inventory/reservations" },
      "https://mock.example",
      "Linux",
      false
    );
    expect(command).toContain("--header 'Content-Type: application/json; charset=utf-8'");
    expect(command).toContain('--data-raw \'{"sku":"SKU-1001","quantity":2,"warehouseId":"WH-DEMO-01"}\'');
    expect(command).not.toContain("\n");
  });

  it("quotes shell metacharacters without changing the configured path or origin", () => {
    const special = { ...endpoint, path: "//items/o'hare/$value;`test`" };
    expect(createEndpointCurlCommand(special, "https://mock.example", "Win32", false)).toContain(
      "--url 'https://mock.example//items/o''hare/$value;`test`'"
    );
    expect(createEndpointCurlCommand(special, "https://mock.example", "Linux", false)).toContain(
      "--url 'https://mock.example//items/o'\"'\"'hare/$value;`test`'"
    );
  });

  it("escapes typographic apostrophes as PowerShell delimiters", () => {
    const command = createEndpointCurlCommand(
      { ...endpoint, path: "/items/left\u2018middle\u2019right" },
      "https://mock.example",
      "Win32",
      false
    );
    expect(command).toContain("--url 'https://mock.example/items/left\u2018\u2018middle\u2019\u2019right'");
  });
});
