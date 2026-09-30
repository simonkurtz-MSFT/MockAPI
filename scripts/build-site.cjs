const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const output = path.join(root, "artifacts", "site");
const indexSource = path.join(root, "site", "index.html");
const projectSource = path.join(root, "src", "MockAPI", "MockAPI.csproj");
const assets = {
  "index.html": "site/index.html",
  "site.css": "site/site.css",
  "site.js": "site/site.js",
  "robots.txt": "site/robots.txt",
  "sitemap.xml": "site/sitemap.xml",
  "brand-mark.svg": "src/MockAPI/wwwroot/brand-mark.svg",
  "dashboard.png": "docs/images/site-dashboard-overview.png",
  "dashboard-endpoints.png": "docs/images/site-dashboard-endpoints.png",
  "dashboard-request-log.png": "docs/images/site-dashboard-request-log.png",
};

function readApplicationVersion() {
  const project = fs.readFileSync(projectSource, "utf8");
  const match = project.match(/<Version>([^<]+)<\/Version>/);
  if (!match) throw new Error("MockAPI.csproj must define the application Version.");
  return match[1];
}

function renderIndex(buildDate = new Date()) {
  if (!(buildDate instanceof Date) || Number.isNaN(buildDate.valueOf())) {
    throw new TypeError("The documentation site build date must be a valid Date.");
  }

  const isoBuildDate = buildDate.toISOString();
  const displayBuildDate = isoBuildDate.replace("T", " ").replace(/\.\d{3}Z$/, " UTC");
  return fs
    .readFileSync(indexSource, "utf8")
    .replaceAll("{{VERSION}}", readApplicationVersion())
    .replaceAll("{{BUILD_DATE_ISO}}", isoBuildDate)
    .replaceAll("{{BUILD_DATE_DISPLAY}}", displayBuildDate);
}

function buildSite({ buildDate = new Date() } = {}) {
  fs.mkdirSync(output, { recursive: true });
  for (const entry of fs.readdirSync(output)) {
    if (!Object.hasOwn(assets, entry)) {
      throw new Error(`Unexpected file in Pages output: ${entry}. Review and remove it before publication.`);
    }
  }
  for (const [destination, source] of Object.entries(assets)) {
    const destinationPath = path.join(output, destination);
    if (destination === "index.html") {
      fs.writeFileSync(destinationPath, renderIndex(buildDate));
    } else {
      fs.copyFileSync(path.join(root, source), destinationPath);
    }
  }
  console.log("Built the allowlisted static site in artifacts/site.");
}

module.exports = { assets, output, buildSite, readApplicationVersion, renderIndex };
if (require.main === module) buildSite();
