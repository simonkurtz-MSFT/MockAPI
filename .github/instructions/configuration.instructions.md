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
- Keep optional `apiDescriptions` keyed by case-sensitive first-segment paths, independent of endpoint membership.
  Bound metadata to 25 groups and 4,000 characters per description; preserve whitespace and ordinal key ordering.
  Endpoint deletion never removes metadata. Built-in merges add missing descriptions atomically but preserve
  existing custom or empty descriptions, even when forced.
- Require normalized absolute paths and reject `/__mockapi`, `/health`, and their descendants.
- Treat v1 `path` values as exact literals. Introduce route templates or alternate matchers only through an explicit versioned contract that preserves existing exact-path semantics.
- Build the v1 matching key only from normalized HTTP method and exact path. Query strings, request headers, and request bodies must not affect matching; prove this with an integration test when implementing dispatch.
- Model response headers as arrays of strings so repeated values round-trip without loss.
- Keep rolling-window rate limits optional, restricted to terminal `429` responses, and configured with a `2xx` pre-limit response.
- Reject control characters and server-controlled or hop-by-hop headers.
- Keep response bodies as raw strings; do not normalize JSON-shaped payload text.
- Apply practical, tested limits to documents, endpoints, methods, headers, and bodies.
- Keep schema validation and semantic validation distinct. The semantic layer owns cross-record method/path conflicts and reserved-route checks.
- Keep serialization deterministic and verify generated configuration against the checked-in schema.
- Keep the built-in example document as a checked-in, schema-valid embedded resource. Expose it read-only and activate it only through the normal validated, ETag-protected merge path.
- Any contract change must update the schema, models, source-generated JSON metadata, examples, management API/dashboard behavior, documentation, and tests together. Use the `mock-endpoint-change` skill.
