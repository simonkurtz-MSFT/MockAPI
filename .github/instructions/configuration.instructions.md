---
description: "Use when changing MockAPI JSON configuration, JSON Schema, examples, import/export, serialization, endpoint matching, or persistence behavior."
name: "MockAPI Configuration"
applyTo: "{config,schemas}/**/*.json"
---

# Configuration Instructions

- Use JSON Schema Draft 2020-12 and keep the schema version explicit.
- Reject unknown properties unless a versioned compatibility decision deliberately permits them.
- Keep endpoint IDs stable UUIDs and method arrays non-empty and unique.
- Require normalized absolute paths and reject `/__mockapi`, `/health`, and their descendants.
- Model response headers as arrays of strings so repeated values round-trip without loss.
- Reject control characters and server-controlled or hop-by-hop headers.
- Keep response bodies as raw strings; do not normalize JSON-shaped payload text.
- Apply practical, tested limits to documents, endpoints, methods, headers, and bodies.
- Keep schema validation and semantic validation distinct. The semantic layer owns cross-record method/path conflicts and reserved-route checks.
- Keep serialization deterministic and verify generated configuration against the checked-in schema.
- Any contract change must update the schema, models, source-generated JSON metadata, examples, management API/dashboard behavior, documentation, and tests together. Use the `mock-endpoint-change` skill.
