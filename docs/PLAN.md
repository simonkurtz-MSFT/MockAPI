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

The new project should target .NET 10 and preserve the small, non-root, chiseled-container approach while replacing hard-coded routes and counters with a general runtime endpoint registry.

## 3. Scope

### Initial release

- Runtime CRUD and enable/disable operations through an internal management API.
- Dashboard for endpoint management and statistics.
- Startup loading from JSON.
- Explicit save, import, and export operations.
- Schema validation before a configuration becomes active.
- Dynamic mock dispatch without process restart.
- Per-endpoint and aggregate in-memory statistics.
- Self-contained Linux x64 publication in a .NET 10 Ubuntu Noble chiseled runtime-deps image.
- Health/readiness endpoints.
- Automated unit, integration, schema, dashboard, and container tests.

### Deferred unless needed during implementation

- Authentication and authorization beyond an optional management API key.
- Durable statistics across restarts.
- Request-body/header matching, route parameters, templating, delays, dropped connections, throttling scenarios, or response sequences.
- Multi-user editing and distributed synchronization across replicas.
- TLS termination inside the container.
- Additional architectures such as `linux-arm64`.

The configuration format should be versioned and extensible so deferred response behaviors can be added without replacing the core endpoint model.

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

- Match the HTTP method case-insensitively and the normalized path case-sensitively by default, consistent with URL path semantics.
- Require paths to start with `/`.
- Ignore query strings for initial route matching; query values remain visible in statistics only if explicitly added later with privacy controls.
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
- `name` is non-empty and length-limited.
- `methods` is a non-empty unique array of valid HTTP method tokens.
- `path` is length-limited, starts with `/`, and cannot target a reserved route.
- `statusCode` is an integer from 100 through 599.
- `reasonPhrase` is optional and disallows control characters. HTTP/2 and HTTP/3 do not transmit reason phrases, so the dashboard and documentation must not imply otherwise.
- `headers` maps a header name to an array of string values so repeated headers round-trip correctly.
- Reject controlled or hop-by-hop headers, including `Connection`, `Content-Length`, `Date`, `Host`, `Server`, `Transfer-Encoding`, and `Upgrade`. The server owns protocol framing and default headers.
- `contentType` is optional but required by validation when a non-empty body is present unless a documented default is applied.
- `body` is a raw string. JSON payloads are stored as JSON text so exact response bytes and invalid-JSON test cases remain possible.
- Define practical limits for document size, endpoint count, methods per endpoint, header count/value size, and body size.

JSON Schema validates document shape. A second semantic validator must detect cross-record conflicts such as duplicate method/path pairs and reserved paths.

## 7. Loading, Saving, Import, and Export

- Resolve the configuration path from `MockApi__ConfigurationPath`, defaulting to `/data/mockapi.json` in the container and a local development path outside it.
- On startup, load and validate the configured file when it exists.
- Make missing-file behavior explicit through `MockApi__AllowEmptyConfiguration`; default to an empty valid registry for local use.
- Fail startup on malformed or semantically invalid configured JSON rather than serving an unintended partial configuration.
- Runtime edits update the active in-memory snapshot immediately.
- Save through an explicit command and optionally through configurable autosave; default autosave to enabled for a single-instance local container.
- Save atomically by writing a temporary file in the same directory, flushing it, and replacing the target.
- Serialize deterministically for readable diffs: stable endpoint order, consistent property order, and indented JSON.
- Import validates the entire candidate document and presents all actionable errors before replacement.
- Export downloads the active configuration without mutating the configured file.
- Require a writable `/data` volume for persistence; document that ephemeral container storage loses changes.
- Use an optimistic configuration revision/ETag on management writes so stale dashboard tabs cannot overwrite newer edits silently.

## 8. Management API

Use JSON endpoints under `/__mockapi/api`:

- List and retrieve endpoint definitions.
- Create, replace/edit, and delete an endpoint.
- Enable or disable an endpoint.
- Retrieve aggregate and per-endpoint statistics.
- Reset statistics globally or for one endpoint.
- Validate a candidate configuration without applying it.
- Import and atomically apply a configuration.
- Export the active configuration.
- Save the active configuration to the configured path.
- Return the active configuration revision and persistence status.

Use source-generated `System.Text.Json` contexts to retain trimming compatibility. Return RFC 9457-style problem details for validation, conflict, and persistence errors. Mutating operations should require an optional configured management API key when the dashboard is exposed beyond localhost; the mock routes must remain independently accessible.

## 9. Dashboard

The first screen should be the operational dashboard, not a marketing page.

### Endpoint management

- Dense endpoint table with name, methods, path, status, enabled state, request count, last request, and actions.
- Create/edit form with header rows, status code, reason phrase caveat, content type, and a payload editor.
- Enable/disable toggle, edit action, delete confirmation, and endpoint duplication.
- Filters for name, method, path, status, and enabled state.
- Immediate validation with server-authoritative errors.
- Import, export, validate, save, and unsaved/persistence status controls.
- Copyable endpoint URL and a compact request preview.

### Statistics

- Aggregate request count, matched/unmatched count, response status classes, response bytes, and requests per minute.
- Per-endpoint total requests, last request time, last status, response bytes, and rolling request rate.
- Bounded rolling buckets for recent activity rather than an unbounded request log.
- Server-Sent Events for low-overhead live updates, with polling fallback.
- Reset controls with confirmation.

The UI must be keyboard usable, responsive, and WCAG 2.2 AA compliant across normal, hover, focus, selected, disabled, and error states. Use the existing application’s visual direction once established; avoid introducing a separate design system solely for this tool.

## 10. Statistics Semantics

- Count every request seen by the mock dispatcher.
- Distinguish matched, disabled/unmatched, and failed-to-write requests.
- Attribute requests by stable endpoint ID, not path, so edits do not corrupt identity.
- Use atomic counters for totals and a bounded ring buffer of time buckets for recent rates.
- Do not retain request or response bodies, authorization headers, cookies, or query values.
- Treat statistics as process-local and reset them on restart in the initial release.
- Document that multiple replicas have independent configuration and statistics; run one replica unless a shared store is added later.

## 11. Container and Publication

- Target `net10.0` and pin an approved .NET 10 SDK feature band in `global.json` with an intentional roll-forward policy.
- Publish self-contained for `linux-x64`, single-file, fully trimmed, and ready-to-run only if size/startup measurements justify it.
- Build in `mcr.microsoft.com/dotnet/sdk:10.0-noble`.
- Run in `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled`.
- Run as the built-in non-root `app` user.
- Listen on HTTP port `8080`; terminate TLS at the container host or ingress.
- Disable the Kestrel `Server` response header.
- Use a read-only root filesystem where supported and mount only `/data` writable.
- Add an OCI health check only if the chiseled image has a suitable built-in mechanism; otherwise rely on platform HTTP probes.
- Produce an SBOM and scan the final image in CI.
- Measure compressed image size and cold startup against the two reference images rather than assuming trimming settings are optimal.

Before implementation, confirm the exact generally available .NET 10 image tags and trimming/AOT compatibility against current Microsoft container documentation. Native AOT should be evaluated as a measured optimization, not an initial requirement, because dashboard/static-file and JSON features can constrain it.

## 12. Security and Operational Guardrails

- Bind management routes to the same listener initially, but support disabling the dashboard and management API independently.
- Default container deployment guidance to loopback or a protected network unless management authentication is configured.
- Add optional constant-time API-key validation for management mutations and never persist the key in endpoint configuration.
- Encode response headers through ASP.NET Core header APIs and reject CR/LF characters.
- Encode dashboard-rendered values; never inject configured body/header text as HTML.
- Apply request-size limits and management API rate limits.
- Protect import/save operations from path traversal; the service writes only to its configured file.
- Do not allow endpoint definitions to shadow management or health routes.
- Emit structured application logs for configuration changes and validation/persistence failures, without logging payloads or secrets by default.

## 13. Testing and Validation

### Unit tests

- Path and method normalization.
- Header and status validation.
- Reserved-route and duplicate detection.
- Atomic registry replacement.
- Deterministic serialization and configuration revision behavior.
- Statistics concurrency and bucket rollover.

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

### Schema contract tests

- Validate checked-in examples against the schema.
- Ensure malformed documents, unknown properties, invalid headers, oversized values, and incompatible versions fail with useful errors.
- Verify application serialization output validates against the checked-in schema.

### Dashboard tests

- CRUD, enable/disable, import/export/save, filtering, live statistics, and failure states.
- Keyboard navigation, focus order, labels, dialogs, and automated accessibility checks.
- Responsive visual checks at mobile and desktop sizes after the UI change set is complete.

### Container checks

- Build the Linux image and run it as non-root.
- Verify `/data` persistence across container recreation.
- Verify readiness and liveness behavior.
- Verify the process starts with a read-only root filesystem.
- Record final image size, startup time, and vulnerability scan results.

## 14. Implementation Phases

| Phase | Deliverables | Exit criteria |
| --- | --- | --- |
| 1. Foundation | Solution/project structure, .NET 10 pin, domain model, source-generated JSON, validation, initial schema and examples | Configuration round-trips and all schema/domain validation tests pass |
| 2. Runtime engine | Immutable registry, catch-all dispatcher, configured responses, concurrency-safe statistics | Runtime-created routes work immediately and concurrent mutation tests pass |
| 3. Persistence and management | File store, revisions, CRUD API, import/export/save, health endpoints, problem details | Invalid changes are atomic; saved configuration survives restart |
| 4. Dashboard | Endpoint management UI, payload/header editor, filters, live statistics, accessible states | End-to-end CRUD and statistics workflows pass on mobile and desktop |
| 5. Container and hardening | Chiseled multi-stage image, non-root/read-only operation, limits, optional management key, CI scans | Container acceptance checks pass and size/startup measurements are recorded |
| 6. Documentation and release | README, configuration reference, operating guide, sample Compose file, migration notes from references | A new user can build, run, persist, manage, export, and restore endpoints from documented steps |

## 15. Acceptance Criteria

- [ ] A valid JSON file creates all configured endpoints at startup.
- [ ] An operator can create, edit, delete, enable, and disable endpoints without restarting the process.
- [ ] Every endpoint can return its configured status code, supported reason phrase, custom headers, content type, and exact payload.
- [ ] Invalid or conflicting configuration never partially replaces the active registry.
- [ ] Configuration can be validated, imported, exported, saved atomically, and reloaded after container recreation with a mounted volume.
- [ ] The dashboard exposes aggregate and per-endpoint statistics without retaining sensitive request content.
- [ ] Management writes detect stale revisions.
- [ ] Reserved system routes cannot be shadowed by mock endpoints.
- [ ] The application runs as non-root in a .NET 10 Noble chiseled container on port 8080.
- [ ] The root filesystem can be read-only with `/data` as the sole writable application mount.
- [ ] Unit, integration, schema, dashboard, accessibility, and container checks pass.
- [ ] Documentation clearly distinguishes HTTP reason phrases from response bodies and explains protocol limitations.

## 16. Decisions to Confirm Before Implementation

- Product/repository naming: retain `MockAPI` or choose a more specific public name.
- Whether management authentication is required in the first release or documented as opt-in.
- Whether autosave defaults to enabled or runtime edits remain dirty until explicitly saved.
- Whether endpoint paths need route templates such as `/users/{id}` in the first release; the baseline assumes exact paths.
- Whether request matching beyond method/path belongs in the first release.
- Whether `linux-arm64` must ship alongside `linux-x64`.
- Required limits for endpoint count, payload size, and statistics retention window.
