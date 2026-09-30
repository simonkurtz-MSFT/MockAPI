const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { spawn } = require("node:child_process");
const { assets, buildSite } = require("./build-site.cjs");

buildSite();
const root = path.resolve(__dirname, "..");
const previewUrl = "http://127.0.0.1:4173/MockAPI/";
const types = {
  ".html": "text/html",
  ".css": "text/css",
  ".js": "text/javascript",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".txt": "text/plain",
  ".xml": "application/xml",
};
const server = http.createServer((request, response) => {
  const pathname = new URL(request.url, "http://localhost").pathname;
  const name = pathname === "/MockAPI/" ? "index.html" : pathname.slice("/MockAPI/".length);
  if (!pathname.startsWith("/MockAPI/") || !Object.hasOwn(assets, name)) {
    response.writeHead(404).end("Not found");
    return;
  }
  response.writeHead(200, { "Content-Type": types[path.extname(name)], "Cache-Control": "no-store" });
  // Serve only allowlisted sources so a browser refresh picks up edits without restarting.
  fs.createReadStream(path.join(root, assets[name]))
    .on("error", (error) => {
      console.error(error);
      response.destroy(error);
    })
    .pipe(response);
});
server.on("error", (error) => {
  console.error("Site preview failed:", error);
  process.exitCode = 1;
});
server.listen(4173, "127.0.0.1", () => {
  console.log(`Site preview: ${previewUrl}`);
  console.log("Refresh the browser after editing. Press Ctrl+C to stop.");
  if (!process.argv.includes("--open")) return;
  const command = process.platform === "win32" ? "powershell.exe" : process.platform === "darwin" ? "open" : "xdg-open";
  const args =
    process.platform === "win32" ? ["-NoProfile", "-Command", `Start-Process '${previewUrl}'`] : [previewUrl];
  const browser = spawn(command, args, { stdio: "inherit" });
  browser.on("error", (error) => console.warn(`Could not open the browser. Open ${previewUrl} manually.`, error));
  browser.on("exit", (code) => {
    if (code !== 0) console.warn(`Browser launcher exited with code ${code}. Open ${previewUrl} manually.`);
  });
});
