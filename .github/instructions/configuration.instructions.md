---
description: "Use when changing MockAPI JSON configuration, JSON Schema, examples, import/export, serialization, endpoint matching, or persistence behavior."
name: "MockAPI Configuration"
applyTo: "{config,schemas}/**/*.json"
---

# Configuration Instructions

- Use JSON Schema Draft 2020-12 and keep the schema version explicit.
- Reject unknown properties unless a versioned compatibility decision deliberately permits them.
- Keep endpoint IDs stable UUIDs and method arrays non-empty and unique.
- Keep endpoint descriptions optional freeform text bounded to 4,000 characters and preserve their whitespace through round trips.
- For Trailhead sample endpoints, use two labeled plain-text sections in the existing `description`:
  `Description:` first and `Technical implementation:` second, separated by a blank line.
  Explain the business scenario before the mock's HTTP behavior and limitations; do not require this format for custom endpoints.
- Keep optional `apiDescriptions` keyed by case-sensitive first-segment paths, independent of endpoint membership.
  Bound metadata to 25 groups and 4,000 characters per description; preserve whitespace and ordinal key ordering.
  Endpoint deletion never removes metadata. Built-in merges add missing descriptions atomically but preserve
  existing custom or empty descriptions, even when forced.
- Require normalized absolute paths and reject `/__mockapi`, `/health`, and their descendants.
- Treat v1 `path` values as exact literals. Introduce route templates or alternate matchers only through an explicit versioned contract that preserves existing exact-path semantics.
- Build the v1 matching key only from normalized HTTP method and exact path. Query strings, request headers, and request bodies must not affect matching; prove this with an integration test when implementing dispatch.
- Route matching remains independent of payload contents. Matched mock POST, PUT, and PATCH request bodies are optional; supplied payloads are limited to 1 MiB and non-empty JSON payloads require valid UTF-8 JSON syntax. These runtime checks override configured responses without inspecting business fields. Keep dashboard and portable request exports supplied with valid optional JSON samples (tailor them to built-in examples where useful), never imply those samples are schemas, and describe actual 400/413/415 errors in exported OpenAPI.
- Model response headers as arrays of strings so repeated values round-trip without loss.
- Keep rolling-window rate limits optional, restricted to terminal `429` responses, and configured with a `2xx` pre-limit response.
- Reject control characters and server-controlled or hop-by-hop headers.
- Keep response bodies as raw strings; do not normalize JSON-shaped payload text.
- Apply practical, tested size limits to documents and endpoint fields, methods, headers, and bodies.
  Do not impose an endpoint-count limit on configuration documents, merges, or bulk selections.
- Keep schema validation and semantic validation distinct. The semantic layer owns cross-record method/path conflicts and reserved-route checks.
- Keep serialization deterministic and verify generated configuration against the checked-in schema.
- Copy all three Trailhead sample files (AOAI, inventory, and shipping) into the container build stage
  and embed them unchanged in the application. Keep development-container path filters and packaging
  tests aligned with the sample catalog. Embedding a sample must not activate it or seed persisted data.
- Compose the built-in example from all three checked-in, schema-valid `trailhead-aoai.json`,
  `trailhead-inventory.json`, and `trailhead-shipping.json` embedded resources (41 endpoints).
  Keep those files as the single source of definitions.
  Expose the combined example read-only and activate it only through the validated, ETag-protected merge path.
- Keep the Trailhead Supply examples cohesive under `/aoai`, `/inventory`, and `/shipping`, with assistant, stock, reservation,
  shipment, tracking, and clearly labeled demo-faults fixtures. Explain static behavior, preserve stable IDs,
  and reserve sample HEAD support for one HEAD-only `/<api>/health` endpoint per API, returning an empty 200.
  Inspection endpoints use GET only; sample health routes retain mock API-key protection
  and do not replace the public application health probes.
  Make advertised links resolvable. Never automatically delete or rewrite existing saved endpoints.
  Apply the document-size limit and all semantic validation to the complete merge, including unrelated endpoints.
  Keep the independent API-description limit at 25 groups.
- Any contract change must update the schema, models, source-generated JSON metadata, examples, management API/dashboard behavior, documentation, and tests together. Use the `mock-endpoint-change` skill.
