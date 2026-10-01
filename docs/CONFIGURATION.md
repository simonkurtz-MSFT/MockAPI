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
    "/ex": "This API demonstrates some of MockAPI's capabilities."
  },
  "endpoints": []
}
```

Groups retain the dashboard's existing first-path-segment identity: `/ex`, `/ex/hello`, and
`/ex/orders/42` belong to `/ex`; `/EX` is a different group. Keys are `/` or one absolute segment
of at most 2,048 characters, without whitespace, control characters, backslashes, query strings,
or fragments. Management and health names are reserved. Metadata never creates a mock route.
At most 25 descriptions may be stored, each containing at most 4,000 characters of plain text.
Whitespace and line breaks are preserved. Omitted or null metadata means no descriptions.

Use the circled information button at the right of an API header to preview its description on
hover or keyboard focus. Click it or press Enter/Space to edit. Escape dismisses the preview or
closes the editor. Applying an edit uses the revision captured when the editor opened; an outdated
draft is rejected rather than overwriting newer changes. Applied edits are saved automatically.

Each endpoint also has an information button beside its name instead of an inline description.
Hover or focus to preview; activate it to open the endpoint editor. Both kinds of preview preserve
original casing and line breaks. Descriptions and dashboard response previews are rendered as text:
HTML markup and JavaScript such as `eval(...)` are not interpreted or executed. This does not strip
configured mock response bodies, which must remain faithful to the responses being tested.

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
| `response.statusCode`   | Integer from 100 through 599; required only for `response`             |
| `response.reasonPhrase` | Optional HTTP/1.1 reason phrase without control characters             |
| `response.headers`      | Object whose values are arrays, preserving repeated values             |
| `response.contentType`  | Required when a non-empty body is configured                           |
| `response.body`         | Raw response text, at most 1 MiB as UTF-8                              |
| `response.rateLimit`    | Optional rolling-window condition for a configured 429 response        |

Matching uses only the case-insensitive HTTP method and exact case-sensitive normalized path. Query strings, request headers, and request bodies do not participate. Disabled endpoints and unmatched requests return `404`.

The dashboard shows `ID` followed by the first eight characters of the endpoint UUID beneath each endpoint name. This is a shortened stable identifier, not a hash. Hover over it to see the complete UUID. The ID remains unchanged across edits so statistics and built-in merge comparisons continue to refer to the same endpoint.

Set `requestCount` to preselect how many requests the dashboard sends from the endpoint test blade. Omit it to preselect one request.

`HEAD` returns the configured status and headers without a response body. `OPTIONS` is not generated automatically.

Set `response.rateLimit` on a `429` response to allow `requestLimit` successful requests during a rolling `windowSeconds` interval. Requests up to and including that limit return the configured `successResponse`; later requests in the window return the top-level 429 response. The success status must be from 200 through 299. Counters are process-local, shared by all methods on the endpoint, and reset when configuration is replaced or the process restarts.

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

- Configuration document: 4 MiB and 25 endpoints.
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

If automatic saving fails, changes remain active but unsaved. The dashboard displays an error and enables
**Retry save**; the management API returns a `500` `autosave-failed` problem with the current ETag.
Retry persistence through **Retry save** or `POST /__mockapi/api/configuration/save` with that ETag, not by
repeating the original mutation. A later successful configuration change also saves the complete active
document, including previously unsaved changes. Validation failures, stale writes, conflict previews, and
unchanged built-in merges do not trigger saving.

The dashboard checks response bodies whose content type is `application/json` or uses a `+json` suffix. It prevents applying an endpoint when a non-empty JSON response body is malformed. Configuration files and management API payloads retain the raw response-body contract, including intentionally malformed JSON used to test client behavior.

Uploaded imports replace the entire active document after validation and ETag verification. The built-in example action is different: it merges by stable endpoint ID, adds missing entries, skips identical entries, preserves unrelated endpoints, and requires a conflict preview plus explicit forced update before applying divergent examples.

The checked-in example contains seven `/ex/` endpoints covering `200`, `201`, `204`, `302`, a conditional `429`, and `500` responses plus `/ex/abort-connection`, which intentionally sends no response. `GET /ex/redirect` returns `302 Found` with `Location: /ex/hello`; clients that automatically follow redirects will receive the destination's `200` response. `GET /ex/server-error` returns `500 Internal Server Error` with the JSON body `{"error":"internal server error"}`.

Each example endpoint includes a semantic description explaining its purpose, configured response, and relevant client behavior. The order endpoints simulate creation and deletion without storing or changing orders. View these descriptions through the endpoint information buttons in the dashboard.

## Azure OpenAI Sample

[`config/aoai.json`](../config/aoai.json) provides controlled Azure OpenAI backend outcomes for the APIM inference-failover response-handling matrix. Every route accepts `POST`; request headers and bodies are intentionally ignored because MockAPI v1 matches only the HTTP method and exact path.

| Route                                      | Backend outcome                          |
| ------------------------------------------ | ---------------------------------------- |
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
