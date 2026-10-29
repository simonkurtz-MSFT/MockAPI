"use strict";

// Embed only a generic reconnect page. Never store dashboard HTML, credentials, definitions, or responses.
const offlineHtml = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="theme-color" content="#075ea8">
    <title>MockAPI - Connection unavailable</title>
    <style>
      :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
      body { margin: 0; min-height: 100vh; display: grid; place-items: center; }
      main { max-width: 32rem; padding: 2rem; }
      p { line-height: 1.6; }
      a { display: inline-block; padding: .8rem 1.2rem; background: #075ea8; color: white; border-radius: .4rem; }
      a:focus-visible { outline: 3px solid currentColor; outline-offset: 4px; }
    </style>
  </head>
  <body>
    <main>
      <h1>Connection unavailable</h1>
      <p>MockAPI needs a connection to this instance. Check your network and that the server is running, then try again.</p>
      <p>No endpoint data is available offline. Changes are not queued or replayed.</p>
      <a href="/">Reconnect to MockAPI</a>
    </main>
  </body>
</html>`;

self.addEventListener("activate", (event) => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener("fetch", (event) => {
  const url = new URL(event.request.url);
  // Root scope is necessary for the dashboard, but all mock, management, and health requests bypass us.
  if (
    event.request.method !== "GET" ||
    event.request.mode !== "navigate" ||
    url.origin !== self.location.origin ||
    url.pathname !== "/"
  ) {
    return;
  }

  event.respondWith(
    fetch(event.request, { cache: "no-store" }).catch(
      () =>
        new Response(offlineHtml, {
          status: 503,
          headers: {
            "Content-Type": "text/html; charset=utf-8",
            "Cache-Control": "no-store",
            "Content-Security-Policy":
              "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
          },
        })
    )
  );
});
