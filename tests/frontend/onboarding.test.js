import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const { assets, buildSite, output } = require("../../scripts/build-site.cjs");
const root = path.resolve(import.meta.dirname, "../..");
const read = (file) => fs.readFileSync(path.join(root, file), "utf8");

describe("public onboarding boundaries", () => {
  it("stages only the explicit site assets and rejects unexpected publication content", () => {
    buildSite();
    expect(fs.readdirSync(output).sort()).toEqual([
      "brand-mark.svg",
      "dashboard-endpoints.png",
      "dashboard-request-log.png",
      "dashboard.png",
      "index.html",
      "robots.txt",
      "site.css",
      "site.js",
      "sitemap.xml",
    ]);
    for (const [destination, source] of Object.entries(assets)) {
      expect(fs.readFileSync(path.join(output, destination))).toEqual(fs.readFileSync(path.join(root, source)));
    }
    const unexpected = path.join(output, "unexpected-test-fixture.txt");
    fs.writeFileSync(unexpected, "Not approved for publication");
    try {
      expect(() => buildSite()).toThrow("Unexpected file in Pages output");
    } finally {
      fs.unlinkSync(unexpected);
    }
  });

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
    expect(read(".devcontainer/setup.sh")).not.toContain("cp config/mockapi.json");
    expect(read(".devcontainer/setup.sh")).toContain('source "$NVM_DIR/nvm.sh" --no-use');
    expect(read(".devcontainer/setup.sh")).not.toContain("\r");
    expect(read(".dockerignore")).toMatch(/^\.devcontainer$/m);
    expect(read(".dockerignore")).toMatch(/^site$/m);
  });

  it("keeps publication disabled by default and uploads only the site output", () => {
    const workflow = read(".github/workflows/pages.yml");
    expect(workflow).toContain("vars.ENABLE_PAGES == 'true'");
    expect(workflow).toContain("github.event_name != 'pull_request'");
    expect(workflow).toContain("path: artifacts/site");
    expect(workflow).not.toContain("path: .\n");
  });

  it("keeps Google Tag Manager on the documentation site, not the runtime dashboard", () => {
    const site = read("site/index.html");
    expect(site).toContain('"GTM-N92H54N6"');
    expect(site).toContain("https://www.googletagmanager.com/gtm.js?id=");
    expect(site).toContain("https://www.googletagmanager.com/ns.html?id=GTM-N92H54N6");
    expect(site).not.toContain("G-XQZ0DQP020");
    const dashboard = read("src/MockAPI/wwwroot/index.html");
    expect(dashboard).not.toContain("googletagmanager.com");
    expect(dashboard).not.toContain("GTM-N92H54N6");
  });
});
