const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const output = path.join(root, "artifacts", "site");
const assets = {
  "index.html": "site/index.html",
  "site.css": "site/site.css",
  "robots.txt": "site/robots.txt",
  "sitemap.xml": "site/sitemap.xml",
  "brand-mark.svg": "src/MockAPI/wwwroot/brand-mark.svg",
  "dashboard.png": "docs/images/01-dashboard-overview.png",
};

function buildSite() {
  fs.mkdirSync(output, { recursive: true });
  for (const entry of fs.readdirSync(output)) {
    if (!Object.hasOwn(assets, entry)) {
      throw new Error(`Unexpected file in Pages output: ${entry}. Review and remove it before publication.`);
    }
  }
  for (const [destination, source] of Object.entries(assets)) {
    fs.copyFileSync(path.join(root, source), path.join(output, destination));
  }
  console.log("Built the allowlisted static site in artifacts/site.");
}

module.exports = { assets, output, buildSite };
if (require.main === module) buildSite();
