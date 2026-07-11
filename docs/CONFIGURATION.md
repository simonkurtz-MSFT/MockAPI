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

## Endpoint Definition

| Property | Requirements |
| --- | --- |
| `id` | Stable UUID preserved across edits and import/export |
| `name` | Non-empty display name, at most 200 characters |
| `enabled` | Controls matching without deleting the definition |
| `methods` | One to eight unique valid HTTP method tokens |
| `path` | Exact case-sensitive path beginning with `/`, at most 2,048 characters |
| `response.statusCode` | Integer from 100 through 599 |
| `response.reasonPhrase` | Optional HTTP/1.1 reason phrase without control characters |
| `response.headers` | Object whose values are arrays, preserving repeated values |
| `response.contentType` | Required when a non-empty body is configured |
| `response.body` | Raw response text, at most 1 MiB as UTF-8 |

Matching uses only the case-insensitive HTTP method and exact case-sensitive normalized path. Query strings, request headers, and request bodies do not participate. Disabled endpoints and unmatched requests return `404`.

`HEAD` returns the configured status and headers without a response body. `OPTIONS` is not generated automatically.

## Limits and Reserved Values

- Configuration document: 4 MiB and 25 endpoints.
- Response headers per endpoint: 64.
- Individual UTF-8 header value: 8 KiB.
- Combined UTF-8 header names and values per endpoint: 32 KiB.
- Reserved paths: `/`, `/__mockapi`, `/health`, and their application-owned descendants.
- Controlled or hop-by-hop response headers such as `Connection`, `Content-Length`, `Date`, `Host`, `Server`, `Transfer-Encoding`, and `Upgrade` are rejected.
- Duplicate enabled method/path pairs are rejected across endpoint definitions.

## Loading and Persistence

`MockApi__ConfigurationPath` selects the persisted file. It defaults to `/data/mockapi.json` in the container. `MockApi__AllowEmptyConfiguration=true` permits startup when that file is missing.

Runtime edits affect routing immediately and remain marked as unsaved until the operator selects **Save**. Saving writes and flushes a temporary file in the same directory, then atomically replaces the configured path.

Uploaded imports replace the entire active document after validation and ETag verification. Built-in template and example actions are different: they merge by stable endpoint ID, add missing entries, skip identical entries, preserve unrelated endpoints, and require a conflict preview plus explicit forced update before applying divergent built-ins.

The checked-in template contains no endpoints, so loading it is a harmless no-op and never clears active configuration. The checked-in example contains four `/ex/` endpoints covering `200`, `201`, `204`, and `429` responses.