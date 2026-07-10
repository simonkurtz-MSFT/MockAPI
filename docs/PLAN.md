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

The ARM64 measurements selected Alpine CoreCLR: compressed publication reduced the payload from 20.33 MiB to 14.46 MiB, and the image from 33.89 MB on Noble chiseled to 26.83 MB on Alpine 3.24. The current Alpine 3.24 `runtime-deps` base is the recommended supported base. A shell-free custom runtime worked and reduced the image to 24.60 MB, but the 2.23 MB saving does not currently justify manually maintaining native libraries, certificate assets, SBOM attribution, and architecture parity. Size-optimized Native AOT was compatible but did not produce a smaller equivalent image. Re-evaluate scratch packaging only if image size becomes more important than maintenance and scanning simplicity, or AOT if memory or startup becomes the priority.

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
- Ensure malformed documents, unknown properties, invalid headers, every limit boundary, oversized values, and incompatible versions fail with useful errors.
- Verify application serialization output validates against the checked-in schema.

### Dashboard tests

- CRUD, enable/disable, import/export/save, filtering, live statistics, and failure states.
- Keyboard navigation, focus order, labels, dialogs, and automated accessibility checks.
- Responsive visual checks at mobile and desktop sizes after the UI change set is complete.

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

- Add Dependabot configuration when repository automation is introduced; do not create it during the initial planning/customization phase.
- Cover NuGet packages, Docker base images, and GitHub Actions.
- Configure every Dependabot update ecosystem with a seven-day cooldown (`cooldown.default-days: 7`) so newly released versions are not proposed before the cooldown expires.
- Group compatible updates where practical and validate the configuration before enabling automated pull requests.

## 14. Implementation Phases

| Phase | Deliverables | Exit criteria |
| --- | --- | --- |
| 1. Foundation | Solution/project structure, .NET 10 pin, domain model, source-generated JSON, validation, initial schema and examples | Configuration round-trips and all schema/domain validation tests pass |
| 2. Runtime engine | Immutable registry, catch-all dispatcher, configured responses, concurrency-safe statistics | Runtime-created routes work immediately and concurrent mutation tests pass |
| 3. Persistence and management | File store, revisions, CRUD API, import/export/save, health endpoints, problem details, management OpenAPI document, Swagger UI | Invalid changes are atomic; saved configuration survives restart; management operations are accurately described by OpenAPI |
| 4. Dashboard | Endpoint management UI, payload/header editor, filters, live statistics, accessible states | End-to-end CRUD and statistics workflows pass on mobile and desktop |
| 5. Container and hardening | Minimal multi-stage image, non-root/read-only operation, limits, network-exposure guidance, CI scans, Dependabot configuration with a seven-day cooldown | Container acceptance checks pass, dependency automation is validated, and size/startup measurements are recorded |
| 6. Documentation and release | README, configuration reference, operating guide, sample Compose file, migration notes from references | A new user can build, run, persist, manage, export, and restore endpoints from documented steps |

## 15. Acceptance Criteria

- [ ] A valid JSON file creates all configured endpoints at startup.
- [ ] An operator can create, edit, delete, enable, and disable endpoints without restarting the process.
- [ ] Every endpoint can return its configured status code, supported reason phrase, custom headers, content type, and exact payload.
- [ ] Invalid or conflicting configuration never partially replaces the active registry.
- [ ] Configuration can be validated, imported, exported, saved atomically, and reloaded after container recreation with a mounted volume.
- [ ] The dashboard exposes aggregate and per-endpoint statistics without retaining sensitive request content.
- [ ] Management writes detect stale revisions.
- [ ] OpenAPI accurately describes the management API, excludes runtime-defined mock endpoints, and Swagger UI can be disabled independently.
- [ ] Reserved system routes cannot be shadowed by mock endpoints.
- [ ] Startup loading, management writes, validation, and import consistently enforce the approved document, endpoint, method, path, body, and header limits.
- [ ] Recent-rate statistics retain exactly 60 one-minute buckets without unbounded growth.
- [ ] The application runs as non-root in a .NET 10 Alpine container on port 8080.
- [ ] One multi-platform image index provides functionally equivalent `linux/amd64` and `linux/arm64` images.
- [ ] The application passes container checks with a `0.5` CPU and `256 MiB` limit; deployment examples start with a `0.25` CPU and `128 MiB` request.
- [ ] The root filesystem can be read-only with `/data` as the sole writable application mount.
- [ ] Dependabot covers NuGet, Docker, and GitHub Actions, with a seven-day cooldown applied to every update ecosystem.
- [ ] Unit, integration, schema, dashboard, accessibility, and container checks pass.
- [ ] Documentation clearly distinguishes HTTP reason phrases from response bodies and explains protocol limitations.

## 16. Confirmed Decisions

- Use `MockAPI` as the product, repository, solution, and primary assembly name.
- Do not require management authentication in the initial release; management routes must remain on localhost or a protected network.
- Persist runtime changes only when an operator explicitly saves them. Runtime changes remain active but visibly unsaved until save succeeds.
- Match exact paths in the initial release while keeping the versioned endpoint contract open to future route templates and alternate path-matching modes.
- Match requests by HTTP method and exact normalized path only in the initial release; query strings, request headers, and request bodies do not participate.
- Ship both `linux/amd64` and `linux/arm64` images under one multi-platform image index.
- Use WSLC as the local container CLI; perform unsupported cross-platform, image-index, read-only-root, SBOM, and vulnerability checks in CI.
- Start deployment guidance at a `0.25` CPU and `128 MiB` memory request with a `0.5` CPU and `256 MiB` memory limit, then tune only from measured runtime evidence.
- Limit configuration documents to 4 MiB and 25 endpoints; each endpoint permits a 200-character name, at most 8 methods, a 2,048-character path, a 1 MiB UTF-8 response body, 64 response headers, 8 KiB per UTF-8 header value, and 32 KiB of combined UTF-8 header names and values.
- Retain recent-rate statistics in 60 one-minute buckets for a 60-minute rolling window.

## 17. Implementation Readiness

All initial architectural and product decisions required for Phase 1 are confirmed. Begin with solution scaffolding, the .NET 10 SDK pin, configuration models, source-generated JSON metadata, the Draft 2020-12 schema, checked-in examples, and boundary-focused validation tests.
