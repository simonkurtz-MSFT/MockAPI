# Configuration Reference

MockAPI stores one versioned JSON document containing all runtime endpoint definitions. The active document is validated as a complete candidate and published atomically; invalid changes never partially affect routing.

## Document

```json
{
  "$schema": "../schemas/mockapi.schema.json",
  "schemaVersion": "1.0",
  "endpoints": []
}
```

The authoritative JSON Schema is [schemas/mockapi.schema.json](../schemas/mockapi.schema.json). Unknown properties are rejected.

Mock API-key security is instance-wide, separate from this document, and enabled by default. Configuration
imports and native exports never carry keys, hashes, or protection settings. See
[Mock API keys](OPERATIONS.md#mock-api-keys) for administrator setup, persistence, and secret-free request exports.

## API Descriptions

Optional `apiDescriptions` supplies API/group metadata separately from each endpoint's `description`:

```json
{
  "schemaVersion": "1.0",
  "apiDescriptions": {
    "/inventory": "Trailhead Supply inventory: inspect static stock snapshots, reserve sample items, and exercise warehouse failures."
  },
  "endpoints": []
}
```

Groups retain the dashboard's existing first-path-segment identity: `/inventory`, `/inventory/items`, and
`/inventory/reservations/res-demo-001` belong to `/inventory`; `/INVENTORY` is a different group. Keys are `/` or one absolute segment
of at most 2,048 characters, without whitespace, control characters, backslashes, query strings,
or fragments. Management and health names are reserved. Metadata never creates a mock route.
At most 25 descriptions may be stored, each containing at most 4,000 characters of plain text.
Whitespace and line breaks are preserved. Omitted or null metadata means no descriptions.

Use the circled information button at the right of an API header to preview its description on
hover or keyboard focus. Click it or press Enter/Space to edit. Escape dismisses the preview or
closes the editor. Moving the pointer away from the information button closes the preview, even
when moving onto the preview itself. Applying an edit uses the revision captured when the editor opened; an outdated
draft is rejected rather than overwriting newer changes. Applied edits are saved automatically.

Each endpoint also has an information button beside its name instead of an inline description.
Hover or focus to preview; activate it to open the endpoint editor. Both kinds of preview preserve
original casing and line breaks. The endpoint edit dialog shows its stable Operation ID as a read-only
field; new and duplicate endpoints receive a new ID when applied.
Information previews can grow to 400 pixels tall or 60% of the viewport height, limited by the space
above or below the information button. Activate the button to read longer descriptions in the editor.
Descriptions and dashboard response previews are rendered as text:
HTML markup and JavaScript such as `eval(...)` are not interpreted or executed. This does not strip
configured mock response bodies, which must remain faithful to the responses being tested.

Every Trailhead sample endpoint organizes its existing `description` into two labeled plain-text
sections: **Description:** first, then **Technical implementation:**, separated by a blank line.
The first explains the shopper, support, or fulfillment scenario; the second explains the mock's
HTTP behavior, fixed data, and limitations. Both headings appear in bold in the information preview;
the content remains plain text and editable
in the single Description field. This is a sample-writing convention, not a required format for custom
endpoints, a new configuration property, or automatic business behavior.

Descriptions survive deletion of the group's last endpoint and reappear if endpoints return to that
group. Moving an endpoint does not move API metadata. An empty string deliberately clears the displayed
description and prevents built-in loading from restoring it. Native configuration import replaces
metadata along with endpoints; native export and save preserve it deterministically. Remove a key from
an imported document to forget that group's metadata entirely.

Built-in loading adds only missing descriptions, including during **Force update**; custom and empty
values always win. Endpoint conflicts prevent all changes, including metadata additions. The optional
field extends the existing `1.0` document contract; older documents remain supported, but older MockAPI
binaries that do not know this field reject it.

## Endpoint Definition

| Property                | Requirements                                                           |
| ----------------------- | ---------------------------------------------------------------------- |
| `id`                    | Stable UUID preserved across edits and import/export                   |
| `name`                  | Non-empty display name, at most 200 characters                         |
| `description`           | Optional freeform text, at most 4,000 characters                       |
| `enabled`               | Controls matching without deleting the definition                      |
| `methods`               | One to eight unique valid HTTP method tokens                           |
| `path`                  | Exact case-sensitive path beginning with `/`, at most 2,048 characters |
| `requestCount`          | Optional dashboard test count from 1 through 5; defaults to 1          |
| `response.behavior`     | Optional `response` (default) or `abortConnection`                     |
| `response.delaySeconds` | Optional whole-second delay from 1 through 300 before behavior begins  |
| `response.statusCode`   | Integer from 100 through 599; required only for `response`             |
| `response.reasonPhrase` | Optional HTTP/1.1 reason phrase without control characters             |
| `response.headers`      | Object whose values are arrays, preserving repeated values             |
| `response.contentType`  | Required when a non-empty body is configured                           |
| `response.body`         | Raw response text, at most 1 MiB as UTF-8                              |
| `response.rateLimit`    | Optional rolling-window condition for a configured 429 response        |

Matching uses only the case-insensitive HTTP method and exact case-sensitive normalized path. Query strings, request headers, and request bodies do not participate. Disabled endpoints and unmatched requests return `404`.

After matching, all mock `POST`, `PUT`, and `PATCH` request bodies are optional. Supplied payloads are limited to
1 MiB. Non-empty JSON request payloads (`application/json` and types ending in `+json`, with optional UTF-8
charset) must contain one syntactically valid JSON value, with maximum nesting depth 64. No field names, types,
or business values are checked: objects, arrays, strings, numbers, booleans, and `null` are all accepted. Malformed
JSON returns `400 application/problem+json` before the configured response, delay, rate limit, or connection abort.
Oversized payloads return `413`; unsupported charsets on non-empty JSON payloads return `415`. Invalid
`Content-Type` syntax returns `400`. Non-JSON media types, including requests without a content type, receive size
checks only.
Authentication runs first, reserved management and health routes retain their own contracts, and other mock
methods remain unchanged. These are global runtime rules, not new endpoint configuration fields; `schemaVersion`
remains independent and unchanged. Validation responses are counted against the matched endpoint without retaining
the request content.

Validation reuses bounded byte buffers and reads JSON tokens without building a document tree.
All rented buffers are cleared before being returned to the pool, including after failed or cancelled reads.
These are internal allocation optimizations; payload limits, accepted JSON, and error responses are unchanged.

The test blade and all portable request exports provide optional illustrative JSON samples for
`POST`, `PUT`, and `PATCH`. Trailhead inventory reservation routes use a SKU, quantity, and warehouse;
shipping creation routes use a service, parcel weight, and line items. Other routes use a general
sample item with a name, status, quantity, and metadata. These are examples, not schemas: the backend
checks supplied payload size and non-empty JSON syntax, not field names or business values.
You can edit or clear the sample in the blade to test actual server errors; the blade does not
block malformed payloads.

The dashboard shows `ID` followed by the first eight characters of the endpoint UUID beneath each endpoint name. This is a shortened stable identifier, not a hash. Hover over it to see the complete UUID. The ID remains unchanged across edits so statistics and built-in merge comparisons continue to refer to the same endpoint.

Set `requestCount` to preselect how many requests the dashboard sends from the endpoint test blade. Omit it to preselect one request.

`HEAD` returns the configured status and headers without a response body. `OPTIONS` is not generated automatically.

Set `response.delaySeconds` to wait before the configured response or connection abort begins. The delay
accepts whole seconds from 1 through 300 and is canceled when the client disconnects. Omit the property for
an immediate response. This is useful for testing client-side timeout and cancellation handling.

```json
{
  "delaySeconds": 3,
  "statusCode": 200,
  "headers": {},
  "contentType": "application/json; charset=utf-8",
  "body": "{\"status\":\"finally ready\"}"
}
```

Set `response.rateLimit` on a `429` response to allow `requestLimit` successful requests during a rolling `windowSeconds` interval. Requests up to and including that limit return the configured `successResponse`; later requests in the window return the top-level 429 response. The success status must be from 200 through 299. Counters are process-local, shared by all methods on the endpoint, and reset when configuration is replaced or the process restarts.

The limit applies only to that endpoint definition, not to its API as a whole or to all APIs in the
instance. Each rate-limited endpoint has an independent budget shared by all clients and all HTTP
methods configured on that definition. Exhausting one endpoint's budget does not affect any other
endpoint, even within the same API. Endpoints without `response.rateLimit` have no mock rate-limit
budget; a plain configured `429` response always returns `429`.

Each request that receives the configured success response counts toward the limit for `windowSeconds`
seconds. Once that time has elapsed, it no longer counts, so another request can succeed. Requests that
receive the rate-limit `429` response do not count toward the limit or delay recovery. After a full window
with no requests, the endpoint can accept another `requestLimit` requests.

```json
{
  "statusCode": 429,
  "headers": { "Retry-After": ["10"] },
  "contentType": "application/json; charset=utf-8",
  "body": "{\"error\":\"try again later\"}",
  "rateLimit": {
    "requestLimit": 4,
    "windowSeconds": 10,
    "successResponse": {
      "statusCode": 200,
      "headers": {},
      "contentType": "application/json; charset=utf-8",
      "body": "{\"status\":\"accepted\"}"
    }
  }
}
```

Set `response.behavior` to `abortConnection` to close the connection before any response headers are sent. An aborting response omits `statusCode` and `reasonPhrase`, uses an empty `headers` object and `body`, and sets `contentType` to `null`. Clients observe a transport or network error; there is no HTTP status code.

```json
{
  "behavior": "abortConnection",
  "headers": {},
  "contentType": null,
  "body": ""
}
```

## Limits and Reserved Values

- Configuration document: 4 MiB when UTF-8 encoded, with no endpoint-count limit.
- API descriptions: 25 groups, independently of endpoint count.
- Response headers per endpoint: 64.
- Individual UTF-8 header value: 8 KiB.
- Combined UTF-8 header names and values per endpoint: 32 KiB.
- Reserved paths: `/`, `/__mockapi`, `/health`, and their application-owned descendants.
- Controlled or hop-by-hop response headers such as `Connection`, `Content-Length`, `Date`, `Host`, `Server`, `Transfer-Encoding`, and `Upgrade` are rejected.
- Duplicate enabled method/path pairs are rejected across endpoint definitions.

## Loading and Persistence

`MockApi__ConfigurationPath` selects the persisted file. It defaults to `/data/mockapi.json` in the container. `MockApi__ConfigurationBlobUri` selects Blob persistence instead of the file, and `MockApi__ManagedIdentityClientId` identifies a user-assigned identity for `DefaultAzureCredential`. `MockApi__LogAnalyticsWorkspaceUri` optionally adds a **Log Analytics workspace** link to the dashboard footer. Both URI settings must use absolute HTTPS URLs. `MockApi__AllowEmptyConfiguration=true` permits startup when the selected file or blob is missing.

Runtime edits affect routing immediately and automatically save the complete configuration. This includes
endpoint creation, replacement, deletion, enable/disable and bulk operations, imports, applied built-in merges,
and API description edits. File persistence writes and flushes a temporary file in the same directory, then
atomically replaces the configured path. Blob persistence uploads the complete deterministic document as one
replacement operation. Concurrent saves are serialized and converge on the latest active revision.

File saves, Blob uploads, and unfiltered native exports stream the canonical bytes of one captured
immutable snapshot without first copying the complete document. Public snapshot exports still return
independent byte arrays; filtered native exports and generated formats retain their existing serialization.

Every applied replacement still validates the complete candidate and creates fresh runtime endpoint
instances. For matching stable endpoint IDs, compilation reuses unchanged immutable UTF-8 response
bodies and ordered header projections, including the pre-limit success response. Body comparison
uses bounded stack storage rather than retaining a second UTF-16 copy. Route and metadata edits can
reuse wire data, but status, content type, reason phrase, and delay always come from the new definition.
Rate-limit permits are never shared between revisions: every successful configuration replacement
starts fresh limiters, as before. Rejected or stale candidates leave the active snapshot and its
limiter state untouched. The reuse index is owned by the snapshot, not an unbounded cross-revision cache.

Single-endpoint management mutation responses deserialize only the selected endpoint from the applied
snapshot's canonical JSON. Unrelated endpoint bodies are skipped without materialization. Returned
data stays detached, with canonical header ordering and body text; complete candidate validation,
atomic publication, ETags, and autosave behavior are unchanged.

If automatic saving fails, changes remain active but unsaved. The dashboard displays an error and enables
**Retry save**; the management API returns a `500` `autosave-failed` problem with the current ETag.
Retry persistence through **Retry save** or `POST /__mockapi/api/configuration/save` with that ETag, not by
repeating the original mutation. A later successful configuration change also saves the complete active
document, including previously unsaved changes. Validation failures, stale writes, conflict previews, and
unchanged built-in merges do not trigger saving.

The dashboard checks response bodies whose content type is `application/json` or uses a `+json` suffix. It prevents applying an endpoint when a non-empty JSON response body is malformed. Configuration files and management API payloads retain the raw response-body contract, including intentionally malformed JSON used to test client behavior.

Uploaded imports replace the entire active document after validation and ETag verification. The built-in example action is different: it merges by stable endpoint ID, adds missing entries, skips identical entries, preserves unrelated endpoints, and requires a conflict preview plus explicit forced update before applying divergent examples.

## Loading Sample Files

The repository supplies native configuration documents for
[Trailhead Azure OpenAI-style assistant](../config/trailhead-aoai.json),
[Trailhead inventory](../config/trailhead-inventory.json), and
[Trailhead shipping](../config/trailhead-shipping.json).

All three files are copied into the container build stage and embedded unchanged in the application.
They are not installed as loose runtime configuration files, and embedding does not activate them
or initialize persisted data.

Import a selected file through the dashboard to replace the complete active configuration, or
copy it to a writable instance-data directory and set `MockApi__ConfigurationPath` to that copy
before startup. Set `MockApi__AllowEmptyConfiguration=false` to fail startup if the copy is missing.
Container deployments can supply the copy through their configuration volume. Follow the
[local startup instructions](../README.md#22-run-directly-with-net), replacing the source sample
filename as needed. API-key protection remains enabled by default and is configured separately.

The dashboard's **Load examples** action combines all three files as one validated 41-endpoint example.
Individual files can still be imported independently. Native import replaces rather than merges
configuration; none of these files automatically seeds a fresh instance merely by filename.

## Trailhead Supply Examples

Trailhead Supply is a fictional company with three cohesive API samples:
[AI assistant](../config/trailhead-aoai.json),
[inventory](../config/trailhead-inventory.json) and
[shipping](../config/trailhead-shipping.json). Their 41 endpoint definitions are the single source
for **Load examples**, which composes all three embedded documents without duplicating their content
or changing endpoint IDs. Individual files remain useful for smaller, focused fixtures.

The examples contain fixed assistant completions, stock snapshots, reservations, shipping quotes, shipment and tracking lookups,
a demonstration label, and clearly labeled HTTP and transport faults. All business data is static:
creation and cancellation do not store records, adjust stock, book carriers, or move parcels.
Advertised resource links resolve to configured GET endpoints. See the route tables below.

Each API has one HEAD-only health endpoint: `/aoai/health`, `/inventory/health`, and `/shipping/health`.
They return a fixed `200 OK` with sample headers, no content type, and no body; GET is not configured.
Inspection endpoints use GET only; HEAD on those paths returns `404`.
These are mock routes subject to instance API-key protection, not live dependency checks or substitutes
for the public application probes under `/health/`.

Built-in loading remains an explicit, revision-protected merge. It adds missing endpoints and API
descriptions, preserves custom or deliberately empty descriptions, and requires conflict review
before a forced update. Repeated loading is a no-op. Existing saved endpoints are never automatically
removed or rewritten when the application is upgraded.

There is no endpoint-count limit, so the 41 Trailhead endpoints can coexist with unrelated endpoints.
The complete candidate must still fit the 4 MiB UTF-8 document-size limit and pass all other validation.
An invalid or oversized candidate is rejected without changing the active configuration or discarding
unrelated endpoints. Sample filenames are independent of the persisted instance
filename, which remains `mockapi.json` by default.

## Trailhead Azure OpenAI Sample

[`config/trailhead-aoai.json`](../config/trailhead-aoai.json) models the fictional Trailhead Supply
AI assistant and preserves the Azure OpenAI-style backend outcomes used by the APIM inference-failover
response-handling matrix. Its success response describes the Trail flask stock and sample shipment
from the inventory and shipping fixtures. These are fixed snapshots: no model runs, no business APIs
are queried, and no conversation is stored. The model name, completion ID, and token usage are
illustrative, not a real deployment or measured usage.

The filename, names, and descriptions are Trailhead-specific; existing `/aoai/*` routes, endpoint IDs,
status codes, error envelopes, and scenario headers remain stable. This is a separate 16-endpoint
configuration when imported alone: 15 inference scenarios and one HEAD-only health mock.
**Load examples** merges these 16 endpoints with inventory and shipping for 41 total.

Every inference route accepts `POST` with an optional request body. Supplied non-empty JSON requests must pass
the global syntax checks above; prompt text, request field values, query strings, and ordinary headers
do not select responses because matching uses only the HTTP method and exact path. For example,
send this body to `/aoai/200/chat/completions`:

```json
{
  "messages": [
    {
      "role": "user",
      "content": "How many Trail flasks are available, and what is the status of shipment shp-demo-001?"
    }
  ]
}
```

Changing the prompt does not change the configured answer. The timeout routes return explicit HTTP
statuses without waiting; the 429 route always returns capacity exhaustion rather than counting requests.
Custom 499 represents an already-transformed PTU policy outcome, not a standard Azure OpenAI response.

| Route                                      | Backend outcome                          |
| ------------------------------------------ | ---------------------------------------- |
| `/aoai/health`                             | HEAD-only health mock; empty `200 OK`    |
| `/aoai/200/chat/completions`               | Successful chat-completions response     |
| `/aoai/400/chat/completions`               | Invalid request                          |
| `/aoai/401/chat/completions`               | Backend authentication failure           |
| `/aoai/403/chat/completions`               | Backend authorization failure            |
| `/aoai/404/chat/completions`               | Missing model deployment                 |
| `/aoai/408/chat/completions`               | Explicit HTTP timeout                    |
| `/aoai/409/chat/completions`               | Non-transient conflict                   |
| `/aoai/409-retry-after/chat/completions`   | Transient conflict with `Retry-After: 1` |
| `/aoai/429/chat/completions`               | Capacity exhaustion with `Retry-After`   |
| `/aoai/499/chat/completions`               | Transformed PTU exhaustion               |
| `/aoai/500/chat/completions`               | Internal infrastructure failure          |
| `/aoai/502/chat/completions`               | Bad gateway                              |
| `/aoai/503/chat/completions`               | Service unavailable with `Retry-After`   |
| `/aoai/504/chat/completions`               | Explicit gateway timeout                 |
| `/aoai/transport-failure/chat/completions` | Connection abort with no HTTP response   |

For a recovery test, configure one APIM pool member to use a fault route and a later member to use the `200` route. For an exhausted-chain test, configure every eligible member to use the fault under test. MockAPI returns the selected backend outcome; APIM owns retry counts, circuit breaking, backend selection, caller-visible normalization, and response headers such as `X-Backend-Retry`.

The transport-failure route approximates a backend connection failure by accepting the request and then aborting the connection. It does not reproduce DNS resolution failure, TLS certificate failure, a forwarding timeout caused by a delayed origin, or a caller disconnect. Those cases require the network-level fault origins described by the inference-failover sample.

## Inventory Sample

[`config/trailhead-inventory.json`](../config/trailhead-inventory.json) contains eleven endpoint definitions for the
fictional Trailhead Supply inventory API. SKU-1001 is a trail flask with 30 units on hand, six
reserved, and 24 available; SKU-1002 is an existing day pack with zero stock. The sample
distinguishes an out-of-stock `200` response from an explicitly configured missing-SKU `404`.

| Method | Route                                          | Behavior                                                |
| ------ | ---------------------------------------------- | ------------------------------------------------------- |
| HEAD   | `/inventory/health`                            | Fixed `200 OK`, no body                                 |
| GET    | `/inventory/items`                             | Two products with available quantities and detail links |
| GET    | `/inventory/items/SKU-1001`                    | In-stock snapshot                                       |
| GET    | `/inventory/items/SKU-1002`                    | Out-of-stock snapshot                                   |
| GET    | `/inventory/items/unknown`                     | `404` problem response for this literal path            |
| POST   | `/inventory/reservations`                      | `201`, fixed reservation, and resolvable `Location`     |
| GET    | `/inventory/reservations/res-demo-001`         | Fixed reservation lookup                                |
| DELETE | `/inventory/reservations/res-demo-001`         | `204`, no body                                          |
| POST   | `/inventory/demo-faults/insufficient-stock`    | `409` problem response                                  |
| POST   | `/inventory/demo-faults/invalid-reservation`   | `422` problem response with a quantity error            |
| GET    | `/inventory/demo-faults/warehouse-unavailable` | `503` problem response, `Retry-After: 30`               |

Reservation creation and cancellation do not change stock or lookup responses. The lookup
exists before creation and remains available after cancellation. Request bodies are optional;
supplied non-empty JSON is syntax-checked, but fields do not select a SKU, quantity, or outcome.
Use the explicit fault paths to test business errors. The warehouse outage does not recover
automatically after its suggested retry interval.

## Shipping Sample

[`config/trailhead-shipping.json`](../config/trailhead-shipping.json) contains fourteen endpoint definitions for a
fictional Trailhead Supply shipping API. It provides fixed standard and express quotes, a
label-created shipment, a separate delivered tracking scenario, and a text-only demonstration
label without customer addresses or valid postage.

| Method | Route                                        | Behavior                                                                       |
| ------ | -------------------------------------------- | ------------------------------------------------------------------------------ |
| HEAD   | `/shipping/health`                           | Fixed `200 OK`, no body                                                        |
| GET    | `/shipping/rates`                            | Fixed standard and express quotes in USD                                       |
| POST   | `/shipping/shipments`                        | `201`, shipment details, and resolvable `Location`                             |
| GET    | `/shipping/shipments/shp-demo-001`           | Fixed shipment                                                                 |
| GET    | `/shipping/tracking/TRACK-DEMO-001`          | Fixed label-created tracking history                                           |
| GET    | `/shipping/tracking/TRACK-DEMO-DELIVERED`    | Separate fixed delivered tracking history                                      |
| GET    | `/shipping/shipments/shp-demo-001/label.txt` | Text attachment marked not valid for shipment                                  |
| DELETE | `/shipping/shipments/shp-demo-001`           | `204`, no body                                                                 |
| POST   | `/shipping/demo-faults/invalid-shipment`     | `422` problem response with a parcel-weight error                              |
| POST   | `/shipping/demo-faults/service-unavailable`  | `503` problem response, `Retry-After: 15`                                      |
| GET    | `/shipping/tracking/unknown`                 | `404` problem response for this literal path                                   |
| GET    | `/shipping/demo-faults/tracking-rate-limit`  | Two `200` responses per rolling ten seconds, then `429` with `Retry-After: 10` |
| GET    | `/shipping/demo-faults/carrier-timeout`      | One-second delay, then an explicit HTTP `504` problem response                 |
| GET    | `/shipping/demo-faults/connection-failure`   | Accepted connection aborted without an HTTP response                           |

Creation and cancellation do not store or modify shipments, tracking history, or labels.
Request bodies are optional; supplied non-empty JSON is syntax-checked without inspecting
parcel or address fields. Query strings never select a service, rate, or tracking number.
The dashboard preselects three requests for the rate-limit scenario. Its counters are
process-local; the tracking snapshot is static. The delayed `504` is an HTTP response, not
a client-side timeout. The connection fault does not reproduce DNS or TLS failures.
