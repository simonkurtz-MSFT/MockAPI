import { createRequire } from "node:module";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const { getExecutableInvocation } = require("../../scripts/lib/process-runner.cjs");

describe("dependency process runner", () => {
  it("invokes executables directly outside Windows", () => {
    expect(
      getExecutableInvocation("pnpm", ["update", "--config.minimum-release-age=11520"], {
        platform: "linux",
      })
    ).toEqual({
      executable: "pnpm",
      arguments_: ["update", "--config.minimum-release-age=11520"],
    });
  });

  it("uses ComSpec for safe pnpm tokens on Windows", () => {
    expect(
      getExecutableInvocation("pnpm", ["add", "@scope/tool@1.2.3"], {
        platform: "win32",
        comSpec: "C:\\Windows\\System32\\cmd.exe",
      })
    ).toEqual({
      executable: "C:\\Windows\\System32\\cmd.exe",
      arguments_: ["/d", "/s", "/c", "pnpm add @scope/tool@1.2.3"],
    });
  });

  it("rejects Windows shell metacharacters", () => {
    expect(() =>
      getExecutableInvocation("pnpm", ["add", "tool@1.0.0&whoami"], {
        platform: "win32",
        comSpec: "cmd.exe",
      })
    ).toThrow("Refusing to pass an unsafe command token");
  });
});
