# Management API

The management API is rooted at `/__mockapi/api`. It has no authentication in the initial release and must remain on localhost or a protected network.

## Concurrency

Every configuration write requires the latest strong ETag in `If-Match`, for example:

```http
If-Match: "4"
```

Read `/configuration` to obtain the current revision and ETag. A missing precondition returns `428`; a stale revision returns `412`. Validation and revision failures leave the active snapshot unchanged.

Management routes allow 120 requests per client address in a rolling one-minute window. Excess requests return `429` problem details and `Retry-After: 60`. Mock endpoints, health checks, and dashboard assets are outside this policy.

## Routes

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/configuration` | Revision, ETag, and unsaved status |
| `GET` | `/configuration/template` | Read-only built-in empty template |
| `GET` | `/configuration/example` | Read-only built-in examples |
| `POST` | `/configuration/{template\|example}/merge` | Add missing built-ins and skip identical entries |
| `POST` | `/configuration/{template\|example}/merge?force=true` | Apply reviewed built-in conflicts |
| `POST` | `/configuration/validate` | Validate a complete candidate without activation |
| `PUT` | `/configuration/import` | Atomically replace with a complete candidate |
| `GET` | `/configuration/export` | Download the active document |
| `POST` | `/configuration/save` | Persist the active revision |
| `GET`, `POST` | `/endpoints` | List or create endpoint definitions |
| `GET`, `PUT`, `DELETE` | `/endpoints/{id}` | Read, replace, or delete one endpoint |
| `PUT` | `/endpoints/{id}/enabled` | Enable or disable one endpoint |
| `GET` | `/statistics` | Aggregate and per-endpoint statistics |
| `GET` | `/statistics/events` | Server-Sent Events statistics stream |
| `POST` | `/statistics/reset` | Reset all process-local statistics |
| `POST` | `/statistics/endpoints/{id}/reset` | Reset one endpoint's statistics |

## Built-In Merge Results

A successful merge reports `added`, `updated`, `skipped`, the resulting revision, and whether a change was applied. Repeating the same merge is an idempotent `200` response with no revision increment.

A divergent stable ID or a method/path collision returns `409` with a conflict list and no mutations. The dashboard shows this preview before it offers **Force update**. Forced updates replace only the reviewed built-in identities or colliding routes, add missing built-ins, and preserve unrelated endpoints.

## Errors and OpenAPI

Validation, conflict, and persistence failures use RFC 9457-style problem details. Validation errors include a path, code, and actionable message and never expose filesystem details.

The management OpenAPI document is `/__mockapi/openapi/v1.json`; Swagger UI is `/__mockapi/swagger`. Runtime-defined mock routes are intentionally excluded because they change at runtime.