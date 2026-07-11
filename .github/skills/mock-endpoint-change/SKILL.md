---
name: mock-endpoint-change
description: "Implement or review changes to MockAPI endpoint definitions, JSON Schema, runtime routing, configured responses, import/export, persistence, management API, or dashboard editing. Use when an endpoint contract change spans multiple layers."
argument-hint: "Describe the endpoint behavior or configuration contract change"
---

# Mock Endpoint Change

Use this skill when a change affects what an endpoint definition means or how it moves through the system.

## Procedure

1. Read `docs/PLAN.md` and identify the affected route, configuration, security, and acceptance requirements.
2. Trace the current contract through these surfaces:
   - JSON Schema and examples.
   - Domain and persistence models.
   - Source-generated JSON metadata.
   - Structural and semantic validators.
   - Registry snapshot and dispatcher.
   - Import, export, and save behavior.
   - Management API and dashboard.
   - Documentation and tests.
3. State one falsifiable behavioral hypothesis and choose the narrowest test that can disprove it.
4. Add or update that test before or with the smallest implementation change.
5. Keep candidate validation complete and atomic. Publish the document, revision, persistence status, and registry as one state snapshot; never expose a partial replacement.
6. Require the current strong ETag for management writes and prove stale or concurrent writes leave the winning snapshot intact.
7. Preserve stable endpoint IDs, deterministic serialization, raw body text, repeated header values, and reserved system routes.
8. Update every affected contract surface in the same change. Do not leave the schema, runtime, and dashboard describing different formats.
9. Run focused checks, then the broader affected test suites.

## Required Evidence

- Checked-in examples validate against the schema.
- Application-generated configuration validates against the schema and round-trips deterministically.
- Semantic conflicts produce actionable errors and leave the active snapshot unchanged.
- A runtime-created or edited endpoint is invocable immediately without process restart.
- HTTP tests verify exact status, permitted multi-value headers, content type, body bytes, and `HEAD` behavior where relevant.
- Dashboard tests cover the changed field or operation when it is user-facing.
- Management route changes update and test the generated OpenAPI document while keeping runtime-defined mock routes excluded.

If the change alters a project invariant or validation practice, update the nearest `.github` instruction or this skill in the same change.
