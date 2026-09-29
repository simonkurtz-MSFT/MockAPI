const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { assets, output, buildSite } = require("./build-site.cjs");

buildSite();
const types = { ".html": "text/html", ".css": "text/css", ".svg": "image/svg+xml", ".png": "image/png" };
const server = http.createServer((request, response) => {
  const pathname = new URL(request.url, "http://localhost").pathname;
  const name = pathname === "/MockAPI/" ? "index.html" : pathname.slice("/MockAPI/".length);
  if (!pathname.startsWith("/MockAPI/") || !Object.hasOwn(assets, name)) {
    response.writeHead(404).end("Not found");
    return;
  }
  response.writeHead(200, { "Content-Type": types[path.extname(name)], "Cache-Control": "no-store" });
  fs.createReadStream(path.join(output, name))
    .on("error", (error) => {
      console.error(error);
      response.destroy(error);
    })
    .pipe(response);
});
server.listen(4173, "127.0.0.1", () => console.log("Site preview: http://127.0.0.1:4173/MockAPI/"));
