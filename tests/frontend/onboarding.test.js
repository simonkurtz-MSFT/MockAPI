import fs from "node:fs";
import path from "node:path";
import { execFile, execFileSync } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:http";
import { createRequire } from "node:module";
import { promisify } from "node:util";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const { assets, buildSite, output, readApplicationVersion, renderSiteStyles } = require("../../scripts/build-site.cjs");
const root = path.resolve(import.meta.dirname, "../..");
const read = (file) => fs.readFileSync(path.join(root, file), "utf8");

describe("public onboarding boundaries", () => {
  it("stages only the explicit site assets and rejects unexpected publication content", { timeout: 15_000 }, () => {
    const buildDate = new Date("2026-09-30T15:52:05.000Z");
    buildSite({ buildDate });
    expect(fs.readdirSync(output).sort()).toEqual([
      "brand-mark.svg",
      "dashboard-endpoints.webp",
      "dashboard-request-log.webp",
      "dashboard.png",
      "dashboard.webp",
      "index.html",
      "robots.txt",
      "site.css",
      "sitemap.xml",
    ]);
    for (const [destination, source] of Object.entries(assets)) {
      if (destination !== "index.html" && destination !== "site.css") {
        expect(fs.readFileSync(path.join(output, destination))).toEqual(fs.readFileSync(path.join(root, source)));
      }
    }
    expect(fs.readFileSync(path.join(output, "site.css"), "utf8")).toBe(renderSiteStyles());
    const index = fs.readFileSync(path.join(output, "index.html"), "utf8");
    expect(index).toContain(`Version ${readApplicationVersion()}`);
    expect(index).toContain("https://github.com/simonkurtz-MSFT/MockAPI/blob/main/config/trailhead-inventory.json");
    expect(index).toContain("https://github.com/simonkurtz-MSFT/MockAPI/blob/main/config/trailhead-shipping.json");
    expect(index).toContain("https://github.com/simonkurtz-MSFT/MockAPI/blob/main/config/trailhead-aoai.json");
    expect(index).toContain('<time datetime="2026-09-30T15:52:05.000Z">2026-09-30 15:52:05 UTC</time>');
    expect(index).not.toMatch(/\{\{(?:VERSION|BUILD_DATE_(?:ISO|DISPLAY))\}\}/);
    const unexpected = path.join(output, "unexpected-test-fixture.txt");
    fs.writeFileSync(unexpected, "Not approved for publication");
    try {
      expect(() => buildSite({ buildDate })).toThrow("Unexpected file in Pages output");
    } finally {
      fs.unlinkSync(unexpected);
    }
  });

  it("keeps lossless gallery screenshots within a 160 KiB combined transfer budget", () => {
    const screenshots = ["dashboard.webp", "dashboard-endpoints.webp", "dashboard-request-log.webp"];
    let totalBytes = 0;
    for (const name of screenshots) {
      const image = fs.readFileSync(path.join(root, assets[name]));
      expect(image.toString("ascii", 0, 4)).toBe("RIFF");
      expect(image.toString("ascii", 8, 12)).toBe("WEBP");
      expect(image.includes(Buffer.from("VP8L"))).toBe(true);
      totalBytes += image.length;
    }
    expect(totalBytes).toBeLessThanOrEqual(160 * 1024);
  });

  it("tracks the Bash entry point as executable for Linux checkouts", () => {
    const entry = execFileSync("git", ["ls-files", "--stage", "--", "start.sh"], {
      cwd: root,
      encoding: "utf8",
    });
    expect(entry).toMatch(/^100755 /);
  });

  it(
    "retries connection resets and unavailable responses until the development app is ready",
    { timeout: 15_000 },
    async () => {
      const readinessLine = read(".github/workflows/devcontainer.yml")
        .split("\n")
        .find((line) => /^\s*curl /.test(line) && line.includes("/health/ready"));
      expect(readinessLine).toBeDefined();
      const [command, ...arguments_] = readinessLine.trim().split(/\s+/);
      let attempts = 0;
      const server = createServer((request, response) => {
        attempts++;
        if (attempts === 1) {
          request.socket.resetAndDestroy();
          return;
        }
        response.statusCode = attempts === 2 ? 503 : 200;
        response.end(attempts === 2 ? "starting" : "ready");
      });
      server.listen(0, "127.0.0.1");
      await once(server, "listening");
      try {
        const address = server.address();
        arguments_[arguments_.length - 1] = `http://127.0.0.1:${address.port}/health/ready`;
        const { stdout } = await promisify(execFile)(command, arguments_, { timeout: 10_000 });
        expect(stdout).toBe("ready");
        expect(attempts).toBe(3);
      } finally {
        server.closeAllConnections();
        server.close();
        await once(server, "close");
      }
    }
  );

  it("keeps the development workspace non-root without privileged features or automatic public exposure", () => {
    const config = JSON.parse(read(".devcontainer/devcontainer.json"));
    expect(config.image).toMatch(/^mcr\.microsoft\.com\/devcontainers\/dotnet:[^@]+@sha256:[a-f0-9]{64}$/);
    expect(config.remoteUser).toBe("vscode");
    expect(config.forwardPorts).toEqual([5080]);
    expect(config.portsAttributes["5080"].onAutoForward).toBe("notify");
    expect(config.customizations.vscode.settings["remote.autoForwardPorts"]).toBe(false);
    expect(config.mounts).toBeUndefined();
    expect(config.privileged).toBeUndefined();
    expect(config.features).toBeUndefined();
    expect(config.containerEnv.MOCKAPI_NODE_VERSION).toMatch(/^\d+\.\d+\.\d+$/);
    expect(read(".devcontainer/setup.sh")).not.toContain("cp config/trailhead-inventory.json");
    expect(read(".devcontainer/setup.sh")).toContain('source "$NVM_DIR/nvm.sh" --no-use');
    expect(read(".devcontainer/setup.sh")).not.toContain("\r");
    expect(read(".dockerignore")).toMatch(/^\.devcontainer$/m);
    expect(read(".dockerignore")).toMatch(/^site$/m);
    expect(read(".dockerignore")).toMatch(/^docs$/m);
  });

  it("keeps publication disabled by default and uploads only the site output", () => {
    const workflow = read(".github/workflows/pages.yml");
    expect(workflow).toContain("vars.ENABLE_PAGES == 'true'");
    expect(workflow).toContain("github.event_name != 'pull_request'");
    expect(workflow).toContain("path: artifacts/site");
    expect(workflow).not.toContain("path: .\n");
  });

  it("keeps direct Google Analytics on the documentation site, not the runtime dashboard", () => {
    const site = read("site/index.html");
    expect(site).toContain('"G-XQZ0DQP020"');
    expect(site).toContain("https://www.googletagmanager.com/gtag/js?id=");
    expect(site).not.toContain("GTM-N92H54N6");
    expect(site).not.toContain("googletagmanager.com/ns.html");
    const dashboard = read("src/MockAPI/wwwroot/index.html");
    expect(dashboard).not.toContain("googletagmanager.com");
    expect(dashboard).not.toContain("GTM-N92H54N6");
    expect(dashboard).not.toContain("G-XQZ0DQP020");
  });
});
