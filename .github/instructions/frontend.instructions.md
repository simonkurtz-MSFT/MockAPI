---
description: "Use when changing the MockAPI dashboard, frontend JavaScript modules, browser behavior, readability, styles, accessibility, or frontend tests."
name: "MockAPI Frontend"
applyTo: "src/MockAPI/wwwroot/**/*.{js,html,css},tests/{frontend,browser}/**/*.js"
---

# Frontend Instructions

- Keep the dashboard framework-free and served as static assets by the ASP.NET Core application.
- Keep `app.js` focused on page-level composition and orchestration. Put cohesive behavior, state, and lifecycle management in purpose-named importable modules instead of growing the entrypoint; wire those modules together through explicit dependencies.
- Keep endpoint table selection/filter/pagination state in `dashboard-endpoint-table.js`, draft and captured-revision state in `dashboard-editor-dialog.js`, test-request batches and focus restoration in `dashboard-test-blade.js`, and statistics/request-log scope and rendering in `dashboard-statistics.js`. Keep management commands and confirmations in the entrypoint.
- Keep API description drafts and captured revisions in `dashboard-api-description.js`; the endpoint table owns
  group and endpoint information buttons and shared hoverable, Escape-dismissible plain-text popovers.
  Preserve description casing and whitespace; use text nodes, never evaluate or inject configured content as HTML.
  Endpoint information buttons open the existing endpoint editor. Group identity remains the
  case-sensitive first path segment, and live updates must not replace an open draft's revision.
- Give feature controllers explicit inputs, callbacks, and disposal. Use `dashboard-dom.js` event scopes for controller-owned listeners and separate scopes for replaceable rows/charts; delegate header-row removal rather than retaining removed rows. Invalidate asynchronous completions when an editor or blade closes, reopens, or is disposed.
- Prefer descriptive state and handler names, early returns, and named intermediate values. Avoid nested ternaries, implicit coercion tricks, dense expression chains, and large anonymous callbacks when explicit steps make the behavior easier to teach and review.
- Keep event, timer, request, and subscription lifecycles visible. Co-locate setup with cleanup where practical, and explain ordering or stale-update protections when they are not apparent from the control flow.
- Dispose page-owned controllers on non-persisted `pagehide`, not `beforeunload` (which also fires for downloads). Suspend synchronization for back-forward cached pages and resume it on persisted `pageshow`.
- Use one `/__mockapi/api/dashboard/events` stream for configuration and statistics. Poll only for recovery or unsupported SSE; abort work while hidden and reject obsolete responses. Cache endpoint definitions in memory by revision, never in browser storage. Preserve endpoint row controls during statistics-only updates.
- Route management requests through `dashboard-management.js`. Configuration commands pass an explicit captured ETag; editor drafts and confirmations keep their reviewed revision despite live updates. Keep the single-flight guard through the authoritative refresh, surface failures, and never automatically replay rejected writes.
- Consume the synchronizer's explicit completed/failed/inactive refresh result at the command boundary. Background recovery may continue after failure, but a command must not treat a failed or interrupted refresh as successful.
- Serve scripts and styles with private, content-validated caching; keep dashboard HTML and administrative data non-storable. Do not use immutable caching for unversioned asset URLs.
- Add JSDoc to every exported function, factory, and reusable exported value. Document parameters, return shapes, callbacks, lifecycle, side effects, thrown errors, and discriminated result variants where applicable.
- Prefer reusable `@typedef` contracts for shared object shapes. Keep annotations specific enough to help editors and reviewers; do not use `Object` when a meaningful shape is known.
- Keep shared endpoint, configuration, management-problem, and statistics wire shapes in `dashboard-core.js`, aligned with the backend's nullable and omitted fields. Keep named controller/lifecycle types in their owning modules and reuse shared contracts rather than duplicating payload shapes.
- Declare required DOM IDs with their expected HTML tags through `getDashboardElements`; the lookup validates markup and infers precise control types. Preserve concrete element and request-entry types through generic helpers. Keep narrowing assertions at validated or statically owned DOM/JSON boundaries rather than casting whole controllers.
- Do not require comments on trivial private helpers. Add concise comments only for accessibility behavior, synchronization invariants, storage fallbacks, security boundaries, or non-obvious browser behavior.
- Keep pure transformation logic in importable modules with direct Vitest coverage. Inject browser APIs into stateful controllers when doing so makes lifecycle and failure behavior deterministic to test.
- Preserve WCAG 2.2 AA contrast, keyboard access, focus order, dialog semantics, reduced-motion behavior, and light, dark, and high-contrast states.
- Gate side-by-side dashboard panels on the measured command-group width, including gaps; never wrap command groups within a column. Fall back to stacked without overwriting the saved layout preference. Keep endpoint search compact and the collapse control aligned with the first filter row.
- Keep measured layout decisions and resize-listener/observer ownership in `dashboard-layout.js`. Test exact fit boundaries, viewport eligibility, scrollbar/padding deductions, and disposal. Keep preference normalization/persistence in `dashboard-preferences.js`; browser storage access must occur inside its guarded adapter calls.
- Keep the first-paint bootstrap synchronous and inline to avoid theme/collapse flashing. Its conservative layout estimate is intentionally different from measured runtime layout; test the actual inline script against runtime theme/collapse defaults, legacy records, invalid records, and unavailable storage.
- Keep `app.css` as one stylesheet with named sections in cascade order. Share panel chrome and typography tokens, keep component-specific rules local, and preserve the precedence of theme, responsive, and reduced-motion overrides. Do not introduce cascade layers or extra stylesheet requests without an explicit need.
- Align expanded column bottoms through shared grid sizing. Empty endpoint tables and request-log tables must fit the supported column width; use automatic horizontal scrolling only for actual overflow, not permanently visible chart scrollbars.
- Default all panels to expanded when collapse preferences are absent. Honor explicit saved collapse choices on reload and across layout transitions; do not stretch collapsed panels to force column alignment.
- Apply initial collapse preferences in the HTML bootstrap before the dashboard modules load. Keep root collapse attributes synchronized with runtime hidden states so visibility and grid sizing agree from first paint.
- Use observable state or network conditions in browser tests instead of fixed waits. Keep browser automation headless unless visible inspection is explicitly requested.
- Run focused Vitest checks while iterating, then the affected frontend coverage and browser checks when the change set is ready.
- Cover DOM-heavy controller lifecycles with isolated native-document fixtures in `tests/browser/dashboard-controllers.spec.js`, alongside full-dashboard Playwright regressions. Do not add a framework or DOM emulator solely to test these controllers.
- Keep browser scenarios in feature-focused suites and cross-feature checks in `dashboard-orchestration.spec.js`. Import the full-dashboard runner and shared assertions from `dashboard-fixtures.js`; it resets configuration/statistics and waits for readiness. Preserve `@smoke` tags when moving scenarios. The shared backend requires one worker; do not override this in CI.
