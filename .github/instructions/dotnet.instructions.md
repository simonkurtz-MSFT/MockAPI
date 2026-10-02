---
description: "Use when creating or changing .NET, C#, ASP.NET Core, project, build, test, or management API files. Covers architecture, readability, trimming, concurrency, and validation rules."
name: "MockAPI .NET"
applyTo: "**/*.{cs,csproj,sln,props,targets}"
---

# .NET Instructions

- Target .NET 10 and keep nullable reference types and implicit usings enabled.
- Prefer guard clauses, descriptive domain names, and named intermediate values when they make control flow easier to follow. Replace dense LINQ pipelines, nested conditionals, or long fluent chains with clear steps when a junior developer would otherwise need to mentally execute the expression.
- Keep async, disposal, concurrency, and immutable-state ownership explicit. When correctness depends on ordering, snapshot capture, atomicity, or cancellation behavior that the type system does not reveal, explain the invariant close to the code.
- Use ASP.NET Core minimal APIs for transport wiring; keep domain validation and state transitions out of route handlers.
- Map reserved management routes before the dispatcher fallback, keep management edits in a domain service, and require strong revision ETags on every management write.
- Keep management API, dashboard, OpenAPI document, and Swagger UI exposure independently configurable. OpenAPI must describe only reserved management operations, never runtime-defined mock routes.
- Represent the active document, revision, persistence status, and endpoint registry as one immutable configuration snapshot and publish replacements atomically.
- Route every runtime or management configuration mutation through `ConfigurationState`; do not publish routes independently of the active document and revision.
- Load persisted configuration before serving requests, fail startup for required malformed or invalid files, and save by replacing a flushed same-directory temporary file.
- Capture one configuration snapshot per save and clear its dirty state only when that revision is still current.
- Automatically save every applied management configuration mutation, including imports, built-in merges,
  bulk changes, and API descriptions. Activate immediately; on persistence failure keep changes active and
  dirty, return an explicit `autosave-failed` problem, and offer manual save retry without replaying the mutation.
  Serialize saves through the configured store and persist a superseding revision rather than overwriting it.
- Keep request dispatch free of persistence and management concerns.
- Require the instance API key before the mock dispatcher by default; leave health probes public.
  Apply configured administrator credentials to Security Settings, but permit setup when administration is anonymous.
  Keep security persistence separate from endpoint configuration, activate only after a successful save,
  and require the independent current strong security ETag for writes. Never export a key or its hash.
- Use cancellation tokens for asynchronous I/O and propagate them through application boundaries.
- Use UTC timestamps through `TimeProvider` where behavior depends on time.
- Use source-generated `System.Text.Json` contexts for application-owned request, response, and persistence types.
- Document public C# contracts with XML documentation comments. Cover parameters, return values, exceptions, wire semantics, concurrency guarantees, and persistence behavior when they are not obvious from the signature; use `<inheritdoc/>` when a contract is inherited.
- Do not add narration to trivial private helpers. Use implementation comments only for invariants, security decisions, protocol constraints, or reasoning the code cannot express clearly.
- Give every management operation a stable OpenAPI operation ID, concise summary, request-body metadata, typed success responses, and applicable problem responses. Document strong `If-Match` requirements in the operation description and test the generated document.
- Return problem details for management API errors and avoid exposing exceptions or filesystem details.
- Design concurrent statistics structures with bounded memory and test their rollover and contention behavior.
- Avoid reflection-heavy or dynamic features that undermine full trimming. Treat trim warnings as defects unless a narrowly documented suppression is necessary.
- Container publication uses Native AOT; local managed development and diagnostic cross-publication remain CoreCLR. Keep single-file compression conditional on non-AOT publication and exclude native debug symbols at publish time.
- Include source-generated JSON metadata for OpenAPI query-parameter types as well as request and response contracts; exercise optional OpenAPI and Swagger UI against the native binary.
- Add unit tests for domain behavior and integration tests for HTTP/protocol behavior. Include a test proving runtime-created endpoints work without restart when changing dynamic routing.
- Assert application-version presence and SemVer format, never a hard-coded current release. Use the shared `SemanticVersionAssert` test helper and derive any cross-surface equality expectations from assembly metadata so version bumps need no test updates.
- Format touched C# files and run the narrowest affected tests before the full solution test suite.
- Collect backend coverage with `tests/coverage.runsettings`, enforce the checked-in line and branch floors, and retain portable symbols for test instrumentation while removing PDBs from publish output.
