# MockAPI Project Plan

## 1. Goal

Build a container-first .NET 10 application that creates configurable mock HTTP endpoints and lets an operator manage them at runtime or from a persisted JSON configuration file.

Each mock endpoint must be able to define:

- One or more HTTP methods.
- A path.
- Whether the endpoint is enabled.
- An HTTP status code.
- An optional HTTP reason phrase/status message, where the protocol supports it.
- Additional response headers.
- A response body and content type.

The application must also provide a dashboard to create, edit, delete, enable, and disable endpoints; inspect request/response statistics; and import/export endpoint configuration.

## 2. Reference Repositories

The implementation should source proven patterns from these sibling repositories without coupling to them at build or runtime:

- `C:\Dev\simonkurtz-MSFT\MockWebAPI`
  - ASP.NET Core minimal API hosting.
  - Kestrel server-header suppression.
  - Self-contained, single-file, fully trimmed publishing.
  - Multi-stage `runtime-deps:9.0-noble-chiseled` container pattern.
- `C:\Dev\simonkurtz-MSFT\WebApi429`
  - Configuration binding.
  - Custom status codes and response headers.
  - Per-endpoint request state.
  - Source-generated `System.Text.Json` metadata.

The new project should target .NET 10 and preserve the small, non-root, minimal-container approach while replacing hard-coded routes and counters with a general runtime endpoint registry.

## 3. Scope

### Initial release

- Runtime CRUD and enable/disable operations through an internal management API.
- Dashboard for endpoint management and statistics.
- Startup loading from JSON.
- Explicit save, import, and export operations.
- Schema validation before a configuration becomes active.
- Dynamic mock dispatch without process restart.
- Request matching by HTTP method and exact normalized path only.
- Per-endpoint and aggregate in-memory statistics.
- Self-contained `linux-musl-x64` and `linux-musl-arm64` publications in .NET 10 Alpine runtime-deps images, delivered through one multi-platform image index for `linux/amd64` and `linux/arm64`.
- Health/readiness endpoints.
- Automated unit, integration, schema, dashboard, and container tests.

### Deferred unless needed during implementation

- Management authentication and authorization.
- Durable statistics across restarts.
- Query-string, request-header, or request-body matching; route templates or alternate path matchers; response templating; delays; dropped connections; throttling scenarios; or response sequences.
- Multi-user editing and distributed synchronization across replicas.
- TLS termination inside the container.

The configuration format should be versioned and extensible so deferred response behaviors and path-matching modes can be added without replacing existing exact-path endpoint definitions.

## 4. Proposed Architecture

Use one ASP.NET Core application and one deployable container.

### Runtime request flow

1. Map reserved system routes first:
   - Dashboard and static assets under `/__mockapi/`.
   - Management API under `/__mockapi/api/`.
   - Health endpoints under `/health/`.
2. Send all other requests to a mock dispatcher.
3. Normalize the request method and path.
4. Look up an enabled endpoint in an immutable in-memory snapshot keyed by method and normalized path.
5. Write the configured status, permitted custom headers, content type, and raw response bytes.
6. Record statistics in a bounded, concurrency-safe collector.
7. Return `404` when no enabled endpoint matches.

A catch-all dispatcher is preferred over adding conventional ASP.NET endpoints after startup. Configuration mutations build and validate a replacement snapshot, then swap it atomically so in-flight requests continue against a consistent prior snapshot.

### Suggested project boundaries

Keep a single deployable web project, but separate responsibilities in code:

- **Configuration model and validation**: versioned document, endpoint definitions, normalization, uniqueness, limits, and schema validation.
- **Endpoint registry**: immutable active snapshot and atomic replacement.
- **Mock dispatcher**: request matching and response writing only.
- **Configuration store**: load, import, export, and atomic file save.
- **Statistics collector**: lock-efficient counters and bounded rolling time buckets.
- **Management API**: dashboard-facing commands and queries.
- **Dashboard**: static, accessible HTML/CSS/JavaScript served by ASP.NET Core.

Avoid a separate Node.js runtime or frontend server. A small static dashboard keeps the runtime image and operational model simple. Frontend package tooling may be introduced only if its generated output is measurably worth the build complexity and no Node runtime enters the final image.

## 5. Route and Matching Rules

- Match literal normalized paths exactly in the initial release. Do not interpret route parameters, wildcards, or regular expressions.
- Match the HTTP method case-insensitively and the normalized path case-sensitively by default, consistent with URL path semantics.
- Use only the normalized HTTP method and exact normalized path as the v1 matching key. Request headers, query strings, and bodies do not affect matching.
- Require paths to start with `/`.
- Exclude query values from statistics unless a future privacy-reviewed feature explicitly adds them.
- Reject duplicate active method/path pairs across endpoint definitions.
- Reserve `/__mockapi`, `/health`, and their descendants for the application.
- Support standard methods such as `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `HEAD`, and `OPTIONS`; permit other valid HTTP tokens unless restricted for safety.
- Implement `HEAD` with normal header/status behavior and no response body.
- Do not silently generate `OPTIONS` behavior for configured mock routes; use a configured response or the documented application default.
- Apply edits atomically. A validation failure leaves the current active configuration unchanged.

## 6. Configuration Contract

The future configuration asset should be `config/mockapi.json`, with its JSON Schema in `schemas/mockapi.schema.json`. The schema should use JSON Schema Draft 2020-12 and the configuration should reference it through `$schema`.

### Document shape

```json
{
  "$schema": "../schemas/mockapi.schema.json",
  "schemaVersion": "1.0",
  "endpoints": [
    {
      "id": "7b2d425d-75f1-4ded-a74e-503374a7e99e",
      "name": "Rate limited response",
      "enabled": true,
      "methods": ["GET"],
      "path": "/api/rate-limited",
      "response": {
        "statusCode": 429,
        "reasonPhrase": "Too Many Requests",
        "headers": {
          "Retry-After": ["30"],
          "X-Mock-Source": ["MockAPI"]
        },
        "contentType": "application/json; charset=utf-8",
        "body": "{\"error\":\"try again later\"}"
      }
    }
  ]
}
```

### Schema constraints

- `schemaVersion` and `endpoints` are required.
- Unknown properties are rejected initially to catch misspellings.
- `id` is a UUID and remains stable across edits/import/export.
- `name` is non-empty and limited to 200 characters.
- `methods` is a non-empty unique array of at most 8 valid HTTP method tokens.
- `path` is limited to 2,048 characters, starts with `/`, and cannot target a reserved route.
- `statusCode` is an integer from 100 through 599.
- `reasonPhrase` is optional and disallows control characters. HTTP/2 and HTTP/3 do not transmit reason phrases, so the dashboard and documentation must not imply otherwise.
- `headers` maps a header name to an array of string values so repeated headers round-trip correctly.
- Reject controlled or hop-by-hop headers, including `Connection`, `Content-Length`, `Date`, `Host`, `Server`, `Transfer-Encoding`, and `Upgrade`. The server owns protocol framing and default headers.
- `contentType` is optional but required by validation when a non-empty body is present unless a documented default is applied.
- `body` is a raw string limited to 1 MiB when UTF-8 encoded. JSON payloads are stored as JSON text so exact response bytes and invalid-JSON test cases remain possible.
- Limit a configuration document to 4 MiB when UTF-8 encoded and 25 endpoint definitions.
- Limit each endpoint to 64 configured response headers, each individual header value to 8 KiB when UTF-8 encoded, and all configured header names and values for an endpoint to 32 KiB when UTF-8 encoded.
- Reject oversized management requests before parsing where possible, and enforce the same limits during startup loading, validation, import, runtime editing, export, and save so no path can activate an invalid document.

JSON Schema validates document shape. A second semantic validator must detect cross-record conflicts such as duplicate method/path pairs and reserved paths.

## 7. Loading, Saving, Import, and Export

- Resolve the configuration path from `MockApi__ConfigurationPath`, defaulting to `/data/mockapi.json` in the container and a local development path outside it.
- On startup, load and validate the configured file when it exists.
- Make missing-file behavior explicit through `MockApi__AllowEmptyConfiguration`; default to an empty valid registry for local use.
- Fail startup on malformed or semantically invalid configured JSON rather than serving an unintended partial configuration.
- Runtime edits update the active in-memory snapshot immediately and mark the configuration as having unsaved changes.
- Persist runtime edits only through an explicit save command in the initial release. Do not autosave.
- Save atomically by writing a temporary file in the same directory, flushing it, and replacing the target.
- Serialize deterministically for readable diffs: stable endpoint order, consistent property order, and indented JSON.
- Import validates the entire candidate document and presents all actionable errors before replacement.
- Export downloads the active configuration without mutating the configured file.
- Require a writable `/data` volume for persistence; document that ephemeral container storage loses changes.
- Use an optimistic configuration revision/ETag on management writes so stale dashboard tabs cannot overwrite newer edits silently.

### Built-in template and example loading

- Loading a built-in template or example is a merge operation, not a full-configuration import. It must never remove unrelated active endpoints or reset the current configuration.
- Compare built-in endpoints to the active snapshot by stable endpoint ID. Classify each as **missing**, **identical**, or **different**; also detect method/path collisions with active endpoints that have a different ID.
- When every built-in endpoint is missing or identical, atomically add only the missing endpoints and skip identical endpoints. Repeated loads are idempotent and never create duplicates.
- If any endpoint is different or has a method/path collision, return a conflict preview describing the affected endpoints and make no changes at all. Do not add otherwise-missing endpoints from that built-in document in the same attempt.
- Offer a separate explicit **Force update** confirmation from the conflict preview. A forced update applies the built-in version for every reported conflict, adds missing built-in endpoints, skips identical endpoints, and preserves all unrelated endpoints.
- Validate the complete merged candidate and require the current configuration revision/ETag before either normal or forced activation. Apply the result as one immutable snapshot so validation errors, stale revisions, or failed conflict resolution leave the prior snapshot unchanged.
- Keep the empty checked-in template valid: loading a built-in document with no endpoints is an idempotent no-op and does not clear the active configuration.

## 8. Management API

Use JSON endpoints under `/__mockapi/api`:

- List and retrieve endpoint definitions.
- Create, replace/edit, and delete an endpoint.
- Enable or disable an endpoint.
- Retrieve aggregate and per-endpoint statistics.
- Reset statistics globally or for one endpoint.
- Validate a candidate configuration without applying it.
- Import and atomically apply a configuration.
- Preview and atomically apply a built-in template/example merge, with explicit forced conflict resolution.
- Export the active configuration.
- Save the active configuration to the configured path.
- Return the active configuration revision and persistence status.

Use source-generated `System.Text.Json` contexts to retain trimming compatibility. Return RFC 9457-style problem details for validation, conflict, and persistence errors. The initial release does not authenticate management operations, so management routes must be deployed only on localhost or a protected network. The mock routes remain independently accessible.

Generate an OpenAPI document for the management API and provide Swagger UI under the reserved `/__mockapi` route space. Do not include runtime-defined mock endpoints in the management API document. Allow OpenAPI document and Swagger UI exposure to be disabled independently from the management API, and preserve trimming compatibility when selecting and configuring the implementation.

## 9. Dashboard

The first screen should be the operational dashboard, not a marketing page.

### Endpoint management

- Dense endpoint table with name, methods, path, status, enabled state, request count, last request, and actions.
- Create/edit form with header rows, status code, reason phrase caveat, content type, and a payload editor.
- Enable/disable toggle, edit action, delete confirmation, and endpoint duplication.
- Filters for name, method, path, status, and enabled state.
- Immediate validation with server-authoritative errors.
- Import, export, validate, save, and unsaved/persistence status controls.
- Load built-in template/example controls that report added and skipped endpoints, show a no-change result when everything is already present, and require a conflict preview plus explicit **Force update** confirmation before changing divergent entries.
- Copyable endpoint URL and a compact request preview.

### Endpoint test blade

- Add a **Test** action for every endpoint that opens a right-side blade without navigating away from the endpoint table or changing the active configuration.
- Initialize the request from the selected endpoint: choose among its configured methods, use its exact path, and target the current MockAPI origin. Allow query parameters, request headers, and an optional raw request body to be edited before sending.
- Keep test-request state local to the blade. Never persist request headers or bodies, and never copy authorization, cookie, or other sensitive values into configuration, statistics, logs, or browser storage.
- Send requests through the browser to the selected mock endpoint and show the effective method and URL, elapsed time, HTTP status, response headers including repeated values, content type, and raw response body. Treat configured non-2xx responses as completed requests rather than blade failures.
- Provide clear sending, completed, empty-body, network-error, and cancellation states. Prevent duplicate submissions while a request is active and allow the active request to be cancelled when supported.
- Keep the endpoint context visible in the blade, provide copy controls for the request URL and response details, and make repeated test runs possible without reopening it.
- On desktop, anchor the blade to the right edge with the endpoint table remaining visible. On narrow viewports, use the full available width. Trap focus while open, restore focus to the originating **Test** action when closed, support `Escape`, and expose status changes to assistive technology.

### Statistics

- Aggregate request count, matched/unmatched count, response status classes, response bytes, and requests per minute.
- Per-endpoint total requests, last request time, last status, response bytes, and rolling request rate.
- Bounded rolling buckets for recent activity rather than an unbounded request log.
- Server-Sent Events for low-overhead live updates, with polling fallback.
- Reset controls with confirmation.

The UI must be keyboard usable, responsive, and WCAG 2.2 AA compliant across normal, hover, focus, selected, disabled, and error states. WCAG 2.2 AA is the release target because it incorporates and supersedes WCAG 2.0 AA; do not reduce the target to 2.0. Use the existing application’s visual direction once established; avoid introducing a separate design system solely for this tool.

### Comprehensive accessibility support

- Treat every WCAG 2.2 Level A and AA success criterion applicable to the dashboard as a release requirement, including the criteria inherited from WCAG 2.0 and 2.1.
- Provide complete keyboard operation with logical focus order, visible focus indicators, no keyboard traps, skip navigation, focus restoration, and predictable focus movement after create, edit, delete, merge, filter, dialog, toast, and test-blade operations.
- Use semantic landmarks, headings, tables, forms, labels, fieldsets, names, descriptions, relationships, and live regions so controls and state changes are understandable without visual context.
- Preserve programmatic names and state for icon buttons, toggles, validation summaries, loading states, statistics updates, confirmations, conflicts, network errors, and request results.
- Meet AA text and non-text contrast in light, dark, Windows high-contrast/forced-colors, hover, focus, selected, disabled, success, warning, and error states. Do not convey information through color alone.
- Support text resize to 200%, browser zoom to 400%, text spacing overrides, reflow at 320 CSS pixels, portrait/landscape orientation, reduced motion, and system font substitution without loss of content or operation.
- Ensure pointer targets, drag-independent operation, dismissal behavior, error identification, correction guidance, status messages, timeout behavior, and repeated-entry workflows meet applicable WCAG 2.2 requirements.
- Keep page title, language, link purpose, instructions, labels, and help text accurate and consistent. Avoid unexpected context changes on focus or input.
- Test with representative screen-reader/browser combinations and document manual results because automated scanners cannot establish full WCAG conformance.
- Publish an accessibility statement before release that records the WCAG version/level, tested environments, known limitations, contact path, and remediation policy. Do not claim conformance while known A/AA failures remain.

## 10. Statistics Semantics

- Count every request seen by the mock dispatcher.
- Distinguish matched, disabled/unmatched, and failed-to-write requests.
- Attribute requests by stable endpoint ID, not path, so edits do not corrupt identity.
- Use atomic counters for totals and a bounded ring buffer of 60 one-minute buckets, retaining 60 minutes of recent rate data.
- Do not retain request or response bodies, authorization headers, cookies, or query values.
- Treat statistics as process-local and reset them on restart in the initial release.
- Document that multiple replicas have independent configuration and statistics; run one replica unless a shared store is added later.

## 11. Container and Publication

- Target `net10.0` and pin an approved .NET 10 SDK feature band in `global.json` with an intentional roll-forward policy.
- Publish self-contained for `linux-musl-x64` and `linux-musl-arm64`, compressed single-file, fully trimmed, and ready-to-run only if size/startup measurements justify it.
- Publish one multi-platform OCI image index containing `linux/amd64` and `linux/arm64` images. Keep tags and application behavior identical across architectures.
- Build in `mcr.microsoft.com/dotnet/sdk:10.0-alpine3.24`.
- Run in `mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine3.24`.
- Run as the built-in non-root `app` user.
- Listen on HTTP port `8080`; terminate TLS at the container host or ingress.
- Disable the Kestrel `Server` response header.
- Use a read-only root filesystem where supported and mount only `/data` writable.
- Use WSLC for native local image builds, container execution, resource limits, volume persistence, logs, inspection, and statistics. Keep the verified command set and limitations in `docs/WSLC.md`.
- Because WSLC `2.9.3.0` cannot select a target platform or manage multi-platform image indexes, build and test both architecture images and assemble the OCI index in CI using suitable native runners and registry tooling.
- Start deployment examples with a `0.25` CPU and `128 MiB` memory request, and a `0.5` CPU and `256 MiB` memory limit. Treat these as conservative initial defaults, not image metadata or guaranteed capacity requirements.
- Rely on platform HTTP probes rather than adding a shell-based OCI health check.
- Produce an SBOM and scan the final image in CI.
- Measure compressed image size and cold startup for both architectures against the two reference images rather than assuming trimming settings are optimal.
- Measure idle and representative-load CPU and memory use under the initial limits. Increase defaults only when startup, health checks, persistence, dashboard use, or representative mock traffic cannot run reliably within them, and record the evidence for any increase.

The initial ARM64 measurements selected Alpine CoreCLR: compressed publication reduced the payload from 20.33 MiB to 14.46 MiB, and the image from 33.89 MB on Noble chiseled to 26.83 MB on Alpine 3.24. Native AOT was compatible and reduced idle memory from 43.49 MiB to 11.56 MiB, but its smallest image was 27.11 MB, it requires native per-architecture compilation, and .NET 10 has no matching AOT-specific runtime-deps tag. Re-evaluate AOT if memory or startup becomes more important than image size and cross-publication simplicity.

## 12. Security and Operational Guardrails

- Bind management routes to the same listener initially, but support disabling the dashboard and management API independently.
- Do not add management authentication in the initial release. Default container deployment guidance to loopback or a protected network and clearly warn against direct exposure to untrusted networks.
- Encode response headers through ASP.NET Core header APIs and reject CR/LF characters.
- Encode dashboard-rendered values; never inject configured body/header text as HTML.
- Apply request-size limits and management API rate limits.
- Protect import/save operations from path traversal; the service writes only to its configured file.
- Do not allow endpoint definitions to shadow management or health routes.
- Emit structured application logs for configuration changes and validation/persistence failures, without logging payloads or secrets by default.

## 13. Testing and Validation

### Backend unit tests

- Path and method normalization.
- Header and status validation.
- Reserved-route and duplicate detection.
- Atomic registry replacement.
- Deterministic serialization and configuration revision behavior.
- Statistics concurrency and bucket rollover.
- Maintain a test inventory for every production backend type and every deterministic decision branch in configuration, validation, canonicalization, registry, dispatch, statistics, persistence, merge, management, rate-limit, and error-mapping behavior.
- Cover success, boundary, invalid-input, cancellation, stale-revision, exception, overflow, concurrency, and recovery paths at the lowest practical layer. Every backend defect fix must add a focused regression test.
- Require 100% line and branch coverage for deterministic backend domain and service code, subject only to explicit reviewed exclusions defined in the coverage-gate policy. Do not use broad file or namespace exclusions to manufacture coverage.
- Keep transport integration tests separate from unit tests so failures identify whether domain logic or HTTP wiring regressed.

### Integration tests

- Load a valid startup configuration and invoke every response field.
- Create an endpoint through the management API and invoke it immediately without restart.
- Edit, disable, re-enable, and delete an endpoint while requests are active.
- Verify a rejected edit leaves the prior snapshot active.
- Verify status code, multi-value headers, content type, exact body, and `HEAD` behavior.
- Verify reason-phrase behavior over HTTP/1.1 and document its absence over HTTP/2.
- Import, export, save, restart, and reload the same configuration.
- Verify ETag/revision conflict handling.
- Exercise concurrent reads and configuration writes.
- Merge built-in configurations into non-empty active snapshots; verify missing endpoints are added, identical endpoints are skipped, repeated loads are idempotent, and unrelated endpoints are preserved.
- Verify a changed stable ID or method/path collision returns a complete conflict preview and leaves the entire prior snapshot active. Verify explicit forced update applies the built-in versions without removing unrelated endpoints.
- Verify empty built-in documents are no-ops and normal and forced merges reject stale revisions and invalid merged candidates atomically.

### Schema contract tests

- Validate checked-in examples against the schema.
- Ensure malformed documents, unknown properties, invalid headers, every limit boundary, oversized values, and incompatible versions fail with useful errors.
- Verify application serialization output validates against the checked-in schema.

### Dashboard tests

- CRUD, enable/disable, import/export/save, filtering, live statistics, and failure states.
- Load built-in template/example documents into empty and populated configurations; verify added, skipped, no-change, conflict-preview, cancel, and explicit **Force update** states without duplicate or unrelated endpoint loss.
- Open the test blade from each endpoint action, verify request initialization, send bodyless and body-bearing methods, and assert successful, configured non-2xx, empty-body, network-error, cancellation, and repeated-run states.
- Verify query parameters and request headers are sent as entered; repeated response headers, content type, raw body, status, effective URL, and elapsed time are rendered without persisting sensitive request data.
- Verify desktop right-side and narrow-viewport full-width layouts, focus trapping and restoration, `Escape` handling, keyboard operation, and accessible status announcements.
- Keyboard navigation, focus order, labels, dialogs, and automated accessibility checks.
- Responsive visual checks at mobile and desktop sizes after the UI change set is complete.

### Frontend unit tests

- Introduce a pinned, development-only JavaScript test toolchain such as Vitest with a DOM environment. Node.js tooling may run in development and CI but must not enter the published application or runtime container.
- Refactor dashboard behavior into testable modules without adding a frontend runtime framework solely for testing.
- Unit test filtering, formatting, request-header parsing, merge-result messages, problem formatting, form serialization, response rendering, focus restoration, cancellation, stale-state protection, and all success/error state transitions.
- Use deterministic fake timers, fetch stubs, and DOM fixtures. Do not rely on network access, wall-clock delays, test ordering, or shared browser state in unit tests.
- Require tests for every fixed frontend defect and every new branch in dashboard behavior.

### Accessibility validation

- Run an automated accessibility engine such as `@axe-core/playwright` on every principal dashboard state, in addition to semantic DOM assertions and keyboard-only Playwright flows.
- Cover empty, populated, filtered-empty, validation-error, merge-conflict, confirmation, endpoint editor, test-blade request/response/error, statistics, offline, light, dark, forced-colors, reduced-motion, mobile, and desktop states.
- Fail CI on any serious or critical automated violation and on any unreviewed moderate violation. Document narrowly justified rules that cannot be evaluated automatically; do not blanket-disable rules.
- Add automated contrast checks for application-owned color tokens and state combinations, including hover, focus, selected, disabled, warning, error, and high-contrast behavior.
- Complete a manual WCAG checklist using keyboard-only navigation, 200% text size, 400% zoom/reflow, text-spacing overrides, and representative screen readers before each release candidate.
- Store the accessibility report and manual checklist as release evidence, with owner and remediation issue for every accepted limitation.

### Coverage gates

- Collect backend line and branch coverage from the .NET test suite and frontend line, branch, function, and statement coverage from the JavaScript unit suite on every pull request and release build.
- Target 100% line and branch coverage for deterministic backend domain/services and extracted frontend behavior modules. Require explicit, reviewed exclusions for generated code, framework bootstrap, platform interop, or unreachable defensive guards.
- Establish repository-wide initial floors only after measuring the expanded suites; the floor must not be lower than 90% line coverage and 85% branch coverage for either backend or frontend. Raise thresholds toward 100% and never lower them merely to make a build pass.
- Enforce per-file or per-module thresholds on security-, validation-, persistence-, concurrency-, merge-, routing-, and request-execution code so aggregate coverage cannot hide critical gaps.
- Fail the build when coverage falls below a threshold, when a changed critical module loses coverage, when expected coverage files are absent, or when tests are skipped unexpectedly.
- Merge .NET and frontend reports into a human-readable summary while preserving native Cobertura/LCOV artifacts. Publish test results and coverage artifacts for every CI run and add a concise pull-request summary.
- Keep coverage deterministic by excluding generated build output and using stable source paths. Coverage is a risk signal, not a substitute for behavioral assertions, boundary tests, concurrency tests, accessibility validation, or browser automation.

### Playwright browser automation

- Add a pinned Playwright test project that starts an isolated MockAPI process with a temporary configuration and never changes a developer's persisted configuration or container.
- Run end-to-end tests in Chromium at representative desktop and mobile viewports. Include light, dark, forced-colors, reduced-motion, touch, and keyboard-only projects.
- Cover first use, built-in merge/no-op/conflict/force, import/export/save, endpoint CRUD and duplication, enable/disable, all filters, statistics and SSE fallback, stale ETags, validation and network failures, test-blade requests/cancellation/non-2xx/empty bodies, copy actions, dialogs, responsive layout, and persistence after restart.
- Assert visible behavior, accessible names/roles, focus order/restoration, live announcements, URL and download behavior, and absence of unexpected console errors, page errors, failed application requests, overflow, clipping, or overlapping controls.
- Use isolated test data, deterministic clocks where needed, resilient role/label locators, and explicit readiness signals. Prohibit arbitrary sleeps and order-dependent tests.
- Capture trace, screenshot, video, console, and network artifacts on failure. Keep successful runs headless in CI and shard only after proving isolation.
- Add a small Chromium smoke subset for rapid pull-request feedback and run the complete Chromium/accessibility suite before merge and on release builds.
- Quarantine is temporary and issue-linked: a quarantined browser test must retain ownership, reason, expiry, and a failing release gate when the expiry is exceeded.

### Developer CLI example showcase

- Add a dedicated developer CLI action and interactive-menu entry that exercises the running built-in example at `/ex/rate-limited`; keep the existing container smoke test fast and separate.
- Do not silently import or replace the active configuration. Detect when the example endpoint is unavailable and explain how to load the built-in example from the dashboard before rerunning the showcase.
- Treat the expected HTTP `429` response as a successful assertion rather than a native-command failure.
- Verify the HTTP/1.1 reason phrase, `Retry-After: 30`, both `X-Mock-Source` values, JSON content type, and exact `{"error":"try again later"}` response body.
- Send the same request with a query string and verify that query values do not affect exact method/path matching.
- Send representative negative requests, including an unsupported method and an unmatched path, and verify the documented `404` behavior.
- Capture statistics before and after the request set and verify the aggregate and stable endpoint-ID counters increase by the expected amounts without depending on prior totals.
- Print a concise pass/fail table for each behavior and return a nonzero exit code when any assertion fails so the action is useful for demonstrations and troubleshooting.
- Respect the configured CLI port and container name rather than hard-coding the default endpoint.
- Add focused automated tests for response/header assertion helpers and a container integration test that loads the checked-in example, runs the showcase, and verifies its exit code and output.

### Container checks

- Use WSLC for local native ARM64 image builds and runtime checks as documented in `docs/WSLC.md`.
- Build `linux/amd64` and `linux/arm64` images and verify that the multi-platform image index references both.
- Run each architecture on a native runner or documented emulation and verify identical application behavior.
- Run each image as non-root.
- Verify `/data` persistence across container recreation.
- Verify readiness and liveness behavior.
- Verify the process starts with a read-only root filesystem.
- Verify startup, health checks, dashboard use, persistence, and representative mock traffic within the `0.5` CPU and `256 MiB` limits; record idle and loaded CPU and memory observations.
- Record per-architecture image size, startup time, and vulnerability scan results.

### Dependency automation

- Add GitHub Dependabot configuration when repository automation is introduced; do not create it during the initial planning/customization phase.
- Cover NuGet packages, Docker base images, GitHub Actions, and the planned frontend unit/Playwright toolchain.
- Configure every Dependabot update ecosystem with a seven-day cooldown (`cooldown.default-days: 7`) so newly released versions are not proposed before the cooldown expires.
- Enforce a minimum dependency release age of seven complete days for direct additions and upgrades, not only Dependabot proposals. Apply the policy consistently in local setup, lockfile generation, pull-request validation, release builds, and automated update workflows.
- Configure pnpm with `minimumReleaseAge: 10080` minutes when the Phase 7 Node.js toolchain is introduced, and use frozen lockfiles in CI so installs cannot silently select newer packages.
- Add a deterministic dependency-age validation command for ecosystems without an equivalent package-manager control, including NuGet packages, Docker image tags/digests, and GitHub Actions. Fail validation when a newly selected version was first published less than seven days earlier or when publication age cannot be established.
- Pin dependency and action versions through reviewed manifests and lockfiles. Do not bypass the cooldown through floating tags, unpinned setup commands, ad hoc global installs, or direct edits that skip the age validator.
- Permit an emergency exception only for an actively exploitable security issue or a broken upstream dependency that blocks required builds. Require an explicit version-scoped allowlist entry with rationale, approver, evidence link, and expiration; report the exception in CI and remove it as soon as the seven-day window elapses.
- Group compatible updates where practical and validate the configuration before enabling automated pull requests.

### GitHub container automation

- Add a pull-request workflow that builds and tests the container without publishing it.
- Add a release/manual workflow that builds native `linux/amd64` and `linux/arm64` images, verifies both variants, and assembles one multi-platform OCI image index.
- Design publishing for Docker Hub, but do not configure pushes, repository coordinates, credentials, or production tags until the Docker Hub namespace and release policy are approved.
- Keep build and publish responsibilities separable so pull requests never require registry credentials and publishing can remain disabled until explicitly enabled.
- Generate an SBOM, run vulnerability scanning, and retain per-architecture image metadata as release evidence before any image is published.

### GitHub quality automation

- Add separate required jobs for backend unit/integration tests, frontend unit tests, backend/frontend coverage gates, Playwright Chromium smoke tests, the full Chromium matrix, and automated accessibility checks.
- Assign every issue created or reused by repository automation to the professional GitHub account `simonkurtz-MSFT`. Keep the assignee in one clearly named workflow-level environment variable and reapply it when a deduplicated open issue already exists.
- Cache packages and Playwright browsers by lockfile and tool version without caching test results or mutable configuration.
- Cancel superseded pull-request runs while keeping release runs immutable. Use least-privilege permissions and no production credentials for test jobs.
- Upload TRX/JUnit, Cobertura/LCOV, merged coverage, accessibility, Playwright HTML, trace, screenshot, video, console, and network artifacts with documented retention periods.
- Make the fast Chromium smoke and coverage jobs required for pull requests. Make the complete Chromium/accessibility matrix and all coverage thresholds required before merge and on release workflows.
- Run scheduled full-Chromium and accessibility checks to detect browser-engine changes even when application source has not changed.
- Pin every external workflow action to a full immutable commit SHA, preserving the corresponding release version in a comment. Pin container actions by digest and fail lint/CI when a mutable tag, branch, or unpinned container reference appears.

### README usability

- Provide a short, copy-pasteable quick start for both local .NET execution and the supported WSLC container workflow.
- State prerequisites and expected outputs, including the dashboard URL, example endpoint, persistence volume, health endpoints, and stop/restart commands.
- Explain the first-use workflow: load the built-in example or template, invoke an endpoint, edit it, save it, and verify persistence after restart.
- Link to detailed configuration, management API, Swagger UI, security, and troubleshooting documentation without requiring those documents for the basic path.
- Validate the README instructions from a clean checkout on a supported environment before release.

## 14. Implementation Phases

| Phase                         | Deliverables                                                                                                                                                                                                                                                                     | Exit criteria                                                                                                                                                                                                                                                           |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1. Foundation                 | Solution/project structure, .NET 10 pin, domain model, source-generated JSON, validation, initial schema and examples                                                                                                                                                            | Configuration round-trips and all schema/domain validation tests pass                                                                                                                                                                                                   |
| 2. Runtime engine             | Immutable registry, catch-all dispatcher, configured responses, concurrency-safe statistics                                                                                                                                                                                      | Runtime-created routes work immediately and concurrent mutation tests pass                                                                                                                                                                                              |
| 3. Persistence and management | File store, revisions, CRUD API, import/export/save, non-destructive built-in merge and conflict preview, health endpoints, problem details, management OpenAPI document, Swagger UI                                                                                             | Invalid changes are atomic; built-ins merge idempotently without unrelated endpoint loss; saved configuration survives restart; management operations are accurately described by OpenAPI                                                                               |
| 4. Dashboard                  | Endpoint management UI, built-in merge/conflict confirmation, payload/header editor, per-endpoint right-side test blade, filters, live statistics, and complete WCAG 2.2 AA support                                                                                              | End-to-end workflows pass on mobile and desktop; automated and manual accessibility evidence has no unresolved A/AA failures                                                                                                                                            |
| 5. Container and hardening    | Minimal multi-stage image, non-root/read-only operation, limits, network-exposure guidance, developer CLI example showcase, CI scans, Dependabot configuration with a seven-day cooldown, and GitHub container build/release workflows prepared for future Docker Hub publishing | Container acceptance checks and the CLI example showcase pass, dependency automation is validated, build workflows verify both architectures, publishing remains disabled until Docker Hub details are approved, and size/startup measurements are recorded             |
| 6. Documentation and release  | Easy-to-follow README quick starts, configuration reference, operating guide, sample Compose file, troubleshooting, and migration notes from references                                                                                                                          | From a clean checkout, a new user can follow the README to build, run, load an example, persist, manage, export, stop, and restore endpoints                                                                                                                            |
| 7. Quality engineering        | Frontend unit harness, expanded backend unit suite, enforced coverage thresholds, Playwright Chromium smoke/full matrices, accessibility automation, seven-day dependency-age enforcement, manual WCAG evidence, and test-result reporting                                       | Backend and frontend thresholds pass without unexplained exclusions; all selected dependency versions satisfy the cooldown or a reviewed temporary exception; Chromium desktop, mobile, keyboard, and accessibility projects pass; required CI checks block regressions |

## 15. Acceptance Criteria

- [x] A valid JSON file creates all configured endpoints at startup.
- [x] An operator can create, edit, delete, enable, and disable endpoints without restarting the process.
- [x] Every endpoint can return its configured status code, supported reason phrase, custom headers, content type, and exact payload.
- [x] Invalid or conflicting configuration never partially replaces the active registry.
- [x] Configuration can be validated, imported, exported, saved atomically, and reloaded after container recreation with a mounted volume.
- [x] Loading a built-in template or example atomically adds only missing endpoints, skips identical endpoints, never duplicates or removes unrelated endpoints, and makes no changes on conflicts unless the operator explicitly forces the reviewed update.
- [x] The dashboard exposes aggregate and per-endpoint statistics without retaining sensitive request content.
- [x] Every dashboard endpoint has an accessible test blade that can send an editable request to that endpoint and display status, repeated response header values, content type, raw body, effective URL, and elapsed time without persisting sensitive request data.
- [x] Management writes detect stale revisions.
- [x] OpenAPI accurately describes the management API, excludes runtime-defined mock endpoints, and Swagger UI can be disabled independently.
- [x] Reserved system routes cannot be shadowed by mock endpoints.
- [x] Startup loading, management writes, validation, and import consistently enforce the approved document, endpoint, method, path, body, and header limits.
- [x] Recent-rate statistics retain exactly 60 one-minute buckets without unbounded growth.
- [x] The application runs as non-root in a .NET 10 Alpine container on port 8080.
- [x] One multi-platform image index provides functionally equivalent `linux/amd64` and `linux/arm64` images.
- [x] The application passes local native container checks with a `0.5` CPU and `256 MiB` limit; deployment examples start with a `0.25` CPU and `128 MiB` request.
- [x] The root filesystem can be read-only with `/data` as the sole writable application mount.
- [x] The developer CLI can non-destructively showcase the loaded `/ex/rate-limited` example by verifying its status, reason phrase, repeated headers, content type, exact body, query-insensitive matching, negative `404` cases, and statistics deltas with clear pass/fail output.
- [x] Dependabot covers NuGet, Docker, and GitHub Actions, with a seven-day cooldown applied to every update ecosystem.
- [x] Local and CI dependency validation enforces a seven-day minimum release age for NuGet, Docker, GitHub Actions, and frontend/Playwright packages, with only explicit version-scoped, expiring emergency exceptions.
- [ ] GitHub pull-request automation builds and tests containers without registry credentials.
- [x] GitHub release/manual automation builds and verifies `linux/amd64` and `linux/arm64`, assembles the multi-platform image index, and is ready for Docker Hub publishing without enabling pushes before registry details are approved.
- [x] Unit, integration, schema, dashboard, accessibility, local container, native architecture, read-only-root, SBOM, and vulnerability checks pass.
- [ ] A new user can follow the README from a clean checkout to run MockAPI locally or in a container, load an example, invoke it, save changes, and verify persistence.
- [x] Documentation clearly distinguishes HTTP reason phrases from response bodies and explains protocol limitations.
- [ ] The dashboard has documented WCAG 2.2 AA conformance, including WCAG 2.0/2.1 requirements, with no unresolved applicable A or AA failures in automated and manual testing.
- [x] Backend and frontend unit suites cover every deterministic behavior and critical branch, with enforced 100% line and branch coverage for the checked-in backend and extracted frontend production scopes.
- [ ] Pull-request and release builds fail on missing coverage output, threshold regression, unexpected skipped tests, serious/critical accessibility violations, or expired browser-test quarantine.
- [x] Playwright smoke and complete suites pass headlessly in Chromium across desktop/mobile, light/dark, keyboard, reduced-motion, and forced-colors projects.
- [ ] CI retains backend/frontend test results, native and merged coverage reports, accessibility evidence, and Playwright traces/screenshots/videos/console/network diagnostics according to documented retention periods.

## 16. Confirmed Decisions

- Use `MockAPI` as the product, repository, solution, and primary assembly name.
- Do not require management authentication in the initial release; management routes must remain on localhost or a protected network.
- Persist runtime changes only when an operator explicitly saves them. Runtime changes remain active but visibly unsaved until save succeeds.
- Treat built-in template/example loading as an idempotent merge by stable endpoint ID, distinct from replacement import; require a conflict preview and explicit forced update before divergent built-in entries can replace active entries.
- Match exact paths in the initial release while keeping the versioned endpoint contract open to future route templates and alternate path-matching modes.
- Match requests by HTTP method and exact normalized path only in the initial release; query strings, request headers, and request bodies do not participate.
- Ship both `linux/amd64` and `linux/arm64` images under one multi-platform image index.
- Use WSLC as the local container CLI; perform unsupported cross-platform, image-index, read-only-root, SBOM, and vulnerability checks in CI.
- Start deployment guidance at a `0.25` CPU and `128 MiB` memory request with a `0.5` CPU and `256 MiB` memory limit, then tune only from measured runtime evidence.
- Limit configuration documents to 4 MiB and 25 endpoints; each endpoint permits a 200-character name, at most 8 methods, a 2,048-character path, a 1 MiB UTF-8 response body, 64 response headers, 8 KiB per UTF-8 header value, and 32 KiB of combined UTF-8 header names and values.
- Retain recent-rate statistics in 60 one-minute buckets for a 60-minute rolling window.
- Assign workflow-created or workflow-reused issues to `simonkurtz-MSFT`; do not use the personal `simonua` handle for this repository.
- Require every newly selected dependency version to be at least seven complete days old in local and CI workflows; exceptions must be security- or build-blocker-specific, reviewed, version-scoped, and temporary.
- Require immutable 40-character commit SHAs for GitHub Action `uses:` references and SHA-256 digests for container actions; mutable workflow dependency references are prohibited.

## 17. Implementation Readiness

All seven implementation phases are represented in the repository. Evidence includes 210 backend tests with 100% line and branch coverage for the checked-in source-only production scope, 23 frontend tests with 100% statements, branches, functions, and lines for the extracted deterministic dashboard module, 36 passing Chromium desktop/mobile/accessibility-preference browser scenarios, automated axe checks, seven-day dependency-age validation, symbol-free dual-runtime publications, and a 28.10 MB native ARM64 image containing no development or test tooling. GitHub-hosted release evidence verifies native AMD64 and ARM64 images, read-only-root execution, SBOM generation, vulnerability scanning, and a two-platform OCI image index. Remaining release gates are pull-request container automation evidence, artifact-retention review, clean-runner README verification, and the manual assistive-technology checklist in `docs/ACCESSIBILITY.md`. Docker Hub publishing remains intentionally disabled pending namespace, credential, and tag-policy approval.
