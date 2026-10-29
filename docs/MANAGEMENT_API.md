# Management API

## Mock API Security Settings

Mock endpoints require `X-MockAPI-Key` by default; administrative routes continue to use Basic authentication.
Every `/__mockapi/api/security` operation requires configured dashboard administrator credentials.
Without that configuration these operations return `403`, even if other management operations are anonymous.

| Method | Path                          | Behavior                                                                                                |
| ------ | ----------------------------- | ------------------------------------------------------------------------------------------------------- |
| `GET`  | `/__mockapi/api/security`     | Returns `enabled`, `configured`, and the independent security `etag`; never the key or hash.            |
| `PUT`  | `/__mockapi/api/security`     | Applies `{"enabled":true}` or an explicit opt-out with `false`.                                         |
| `POST` | `/__mockapi/api/security/key` | Generates a new key, revokes the previous key, enables protection, and returns `key` and `status` once. |

Security writes require their current strong ETag in `If-Match`: missing headers return `428`, stale or invalid
headers return `412`. Successful changes are persisted before activation; persistence failures return `500`
without changing active authorization. Responses use `Cache-Control: no-store`. Configuration ETags and saves
are independent of security settings.

Request-format exports describe the current security requirement using variables or OpenAPI security schemes.
Native endpoint configuration exports never contain security settings. See
[Mock API keys](OPERATIONS.md#mock-api-keys) for setup and storage details.

The management API is rooted at `/__mockapi/api`. When `MockApi__DashboardUsername` and `MockApi__DashboardPasswordHash` are configured together, HTTP Basic authentication protects it with the other administrative surfaces. Valid Basic credentials establish an `HttpOnly`, `Secure`, `SameSite=Strict` browser session for subsequent administrative requests. The session ends when the browser session or application process ends. Without that credential pair, keep the API on localhost or a protected network unless unauthenticated public access is an explicit deployment choice.

## Concurrency

Every configuration write requires the latest strong ETag in `If-Match`, for example:

```http
If-Match: "4"
```

Read `/configuration` to obtain the current revision and ETag. A missing precondition returns `428`; a stale revision returns `412`. Validation and revision failures leave the active snapshot unchanged.

Applied configuration writes activate immediately and automatically persist the complete document to the
configured file or blob. This includes endpoint CRUD, enabled-state and bulk operations, imports, applied
built-in merges, and API description edits. Validation failures, stale writes, conflict previews, and
unchanged merges do not save.

An automatic persistence failure returns `500` with problem type
`https://mockapi.local/problems/autosave-failed` and the current ETag. **The mutation is already active**,
but `/configuration` reports `hasUnsavedChanges: true`. Do not repeat the mutation: retry
`POST /configuration/save` with the current strong ETag, or use **Retry save** in the dashboard.
A subsequent successful configuration write also persists previously unsaved changes.
Endpoint configuration activation remains independent of saving; security settings still persist before activation.

Management routes allow 120 requests per client address in a rolling one-minute window. Excess requests return `429` problem details and `Retry-After: 60`. Mock endpoints, health checks, and dashboard assets are outside this policy.

## Routes

| Method                 | Route                                     | Purpose                                              |
| ---------------------- | ----------------------------------------- | ---------------------------------------------------- |
| `GET`                  | `/configuration`                          | Revision, ETag, API descriptions, and unsaved status |
| `PUT`                  | `/configuration/api-description`          | Edit one path-based API description                  |
| `GET`                  | `/configuration/example`                  | Read-only built-in examples                          |
| `POST`                 | `/configuration/example/merge`            | Add missing examples and skip identical entries      |
| `POST`                 | `/configuration/example/merge?force=true` | Apply reviewed example conflicts                     |
| `POST`                 | `/configuration/validate`                 | Validate a complete candidate without activation     |
| `PUT`                  | `/configuration/import`                   | Atomically replace with a complete candidate         |
| `GET`                  | `/configuration/export`                   | Download the active MockAPI document                 |
| `GET`                  | `/configuration/export/{format}`          | Download a portable request/test artifact            |
| `POST`                 | `/configuration/save`                     | Retry persistence of the active revision             |
| `GET`, `POST`          | `/endpoints`                              | List or create endpoint definitions                  |
| `GET`                  | `/endpoints/manifest`                     | Ordered endpoint IDs and content hashes              |
| `POST`                 | `/endpoints/batch`                        | Read selected definitions from one revision          |
| `POST`                 | `/endpoints/bulk`                         | Atomically enable, disable, or delete endpoints      |
| `GET`, `PUT`, `DELETE` | `/endpoints/{id}`                         | Read, replace, or delete one endpoint                |
| `PUT`                  | `/endpoints/{id}/enabled`                 | Enable or disable one endpoint                       |
| `GET`                  | `/statistics`                             | Statistics and recent request summaries              |
| `GET`                  | `/statistics/events`                      | Server-Sent Events statistics and request stream     |
| `GET`                  | `/dashboard/events`                       | Configuration changes and coalesced statistics       |
| `POST`                 | `/statistics/reset`                       | Reset all process-local statistics                   |
| `POST`                 | `/statistics/endpoints/{id}/reset`        | Reset one endpoint's statistics                      |

Portable export formats are `postman`, `insomnia`, `curl`, `jmeter`, `openapi`, `k6`, and `http`. Only enabled endpoints are included. The generated artifacts use `http://localhost:8080` as their editable default base URL. Postman, JMeter, and k6 exports include assertions for configured responses; cURL, Insomnia, and HTTP exports provide runnable requests; OpenAPI describes the configured responses.

### Incremental endpoint synchronization

The dashboard retains endpoint definitions **only in memory**, never in `localStorage`, `sessionStorage`,
or a persistent application cache. Administrative responses still use `Cache-Control: no-store`.
A reload starts with an empty endpoint cache; ordinary edits transfer only added or changed definitions.

1. Obtain the current configuration ETag from `/configuration` or its dashboard SSE event.
2. `GET /endpoints/manifest` with that strong ETag in `If-Match`. The response is an ordered array
   of `{"id":"<full endpoint UUID>","contentHash":"<32 uppercase hex characters>"}` for all endpoints,
   including disabled endpoints. Its `ETag` identifies the captured configuration revision.
3. Compare each content hash against the in-memory definition. For added or changed entries,
   `POST /endpoints/batch` with the same `If-Match` and
   `{"endpoints":[{"id":"<full endpoint UUID>","contentHash":"<hash from manifest>"}]}`.
   This POST is read-only; it neither activates nor saves
   configuration. JSON avoids URL-length limits for large selections.
4. Reassemble definitions in manifest order, remove IDs absent from the manifest, and publish the
   complete replacement before using its ETag for edits. Skip the batch entirely when nothing changed.

Content hashes are the first 128 bits of SHA-256 over canonical endpoint JSON, covering the entire
definition, including exact body text and ordered repeated header values. They are change detectors,
not credentials or endpoint identities. Stable UUIDs are never shortened or changed by edits.
API-description edits, endpoint reordering, and persistence-status changes do not change an endpoint's
content hash. Hashes are computed lazily once per immutable snapshot without materializing body strings;
save-only snapshots share the projection. Batch reads materialize only selected definitions.

Both synchronization routes require a current strong `If-Match`: missing headers return `428`,
malformed or weak tags return `400`, and stale revisions return `412`. Every success comes from one
captured immutable snapshot. The batch requires a non-empty manifest selection with unique, non-empty
UUIDs and correctly formatted hashes. Invalid selections return `400`, and any unknown ID rejects
the entire batch with `404`. A mismatched content hash returns `412`, including when a restarted
process reuses a revision between manifest and batch reads. Normal management
request-size, media-type, authentication, and rate limits apply. Returned definitions follow
configuration order, not request order.

If either read fails or its ETag differs from the target, keep the last complete dashboard snapshot
and recover using fresh configuration status and a new manifest, without replaying writes. Reconnects
recheck the manifest even if a restarted process reuses its numeric revision. The unfiltered
`GET /endpoints` and single-endpoint read remain available and unchanged.

Both export routes accept repeated `endpointId` query parameters containing stable endpoint UUIDs. Omitting
`endpointId` exports every endpoint; providing one or more values limits the native document or portable
artifact to those endpoints. The dashboard applies the same rule: Export includes every endpoint when the
selection is empty and only the selected endpoints otherwise.

API description edits accept `{"path":"/inventory","description":"Trailhead Supply inventory overview"}` and require the
editor's captured strong `If-Match` ETag. Both properties are required strings; an empty description
clears its displayed text. Success returns `200` with the complete configuration status, including
`apiDescriptions`. Invalid metadata returns `422` without changing any endpoints or descriptions.
The configuration status and SSE configuration events carry metadata from the same revision.
See [API descriptions](CONFIGURATION.md#api-descriptions) for identity, limits, and persistence rules.

Native export preserves all API metadata. OpenAPI exports represent descriptions as top-level tags,
with matching enabled operations assigned to those tags; individual operation descriptions remain
separate. Other portable request/test formats remain endpoint-focused and omit API-level metadata.

Bulk endpoint requests provide one or more unique stable endpoint IDs and an `operation` of `enable`, `disable`, or `delete`. Every ID must exist. The complete operation uses one configuration revision and leaves the active snapshot unchanged when validation, endpoint lookup, or ETag checks fail.

Statistics snapshots include at most 100 recent logical request summaries in newest-first order,
including unmatched requests with a null endpoint ID and a `404` status. Dashboard-generated retries
with the same request ID are coalesced into one summary whose `transportAttempts` value reports the
physical dispatch count; aggregate and endpoint counters count that logical request once.
Each summary contains the timestamp, method, normalized path, matched endpoint ID, outcome, HTTP
status when available, and completed response byte count. Outcomes are `response` (configured HTTP
response, including configured errors), `aborted` (configured connection abort), `unmatched` (no
enabled method/path match), `rejected` (payload validation returned `400`, `413`, or `415`), and
`failedWrite` (uncompleted matched dispatch). The last three outcomes are labeled **Unexpected**
in the dashboard and can be hidden with **Show unexpected requests**, which defaults to on and is
saved only in this browser. Filtering never changes aggregate statistics or the retained feed.
The information button beside **Show unexpected requests** explains these categories using the
shared dashboard tooltip, available on hover, keyboard focus, or tap.
The process never retains request or response bodies, headers, cookies, authorization values, or
query values. Resetting all statistics clears the feed; resetting one endpoint removes only its summaries.

Cancellation during a configured delay records a matched `failedWrite` attempt with zero completed
bytes and no HTTP status. These attempts contribute to request and failure totals, but not HTTP status
classes. If writing fails after response selection, statistics retain the selected status, including
the success status of a rate-limited endpoint, with zero completed bytes. Intentional connection aborts
remain separate `aborted` outcomes. Existing logical-request retry coalescing applies to failures too.

Rolling statistics use contiguous value-type buckets with six packed atomic counters per minute,
avoiding separate heap objects for every bucket and counter. Recording, snapshot reads, and reset
operate on the stored counters by reference. This changes storage only: the 60-minute history,
counter saturation, bounded endpoint tracking, concurrent non-transactional snapshots, and JSON
contract remain unchanged. No new recording locks or sharded counters are introduced.

## Dashboard Synchronization and Caching

The dashboard opens one authenticated `GET /dashboard/events?deltas=true` connection. It receives a
`configuration` event containing the same JSON shape as `/configuration`, followed by a
`statistics` event containing the same shape as `/statistics`. Configuration events are
triggered by atomic snapshot publication, including saves that clear the dirty flag without
incrementing the revision. Invalid or conflicting writes do not publish events.

Statistics are coalesced to at most one update every two seconds and serialized once per
shared interval frame across connected dashboards. Idle statistics are not retransmitted, except
at minute boundaries to advance the rolling window. Idle streams send keepalive comments
approximately every 16 seconds. Disable proxy buffering and allow long-lived SSE responses.
The existing `/statistics/events` route remains available with its original behavior.

All three statistics delivery paths share one bounded management-side cache keyed by the
collector's version and UTC minute: GET reuses the immutable snapshot, while both SSE routes
reuse its compact JSON. GET retains its indented JSON formatting and serializer options;
the standalone statistics stream still transmits every two seconds even when unchanged.
Recording, retries, and successful resets invalidate the version; an idle minute rollover
advances both aggregate and endpoint histories. The cache reads the version before capturing
counters so a recording or reset completing during capture invalidates the next read.
Snapshots retain the collector's existing concurrent, non-transactional read semantics.

Only management readers acquire the cache lock. Recording never waits for serialization.
The snapshot cache retains one snapshot and, when requested, one compact string. The delta
cache additionally retains one current interval frame, its bounded patch, and lazy serializations;
it does not retain a chain of previous snapshots or a replay queue. A slow connection can hold
its currently transmitting frame until the write completes.
SSE framing writes its prefix, compact JSON, and terminator separately, avoiding a second
full-payload string without changing the event bytes.

Reconnects receive current state; there is no replay queue or `Last-Event-ID` history. The
dashboard reloads endpoint definitions only when their revision changes or after reconnecting
(a restart can reuse a numeric revision). Saves at the same revision update the Save button
without reloading endpoints. Unsupported or failed SSE uses a 15-second completion-scheduled
poll, which stops after stream recovery. Hidden pages close the stream and abort requests;
returning pages reconnect. Explicit mutations still refresh immediately.

### Interval Statistics Deltas

The optional Boolean `deltas=true` query selects interval **state patches**, not raw request-event
replay. Omit it or set it to `false` for the original full `statistics` events without SSE IDs.
The full `/statistics` API and standalone `/statistics/events` stream are unchanged.

Opt-in statistics frames carry an SSE `id`. The initial `statistics` event contains a complete
baseline. Subsequent `statistics-delta` JSON has these fields:

| Field                | Meaning                                                                  |
| -------------------- | ------------------------------------------------------------------------ |
| `baseSequence`       | Sequence of the full view this patch updates                             |
| `sequence`           | New sequence, also carried by the SSE `id`                               |
| `windowStartUtc`     | Oldest UTC minute in the retained 60-minute window                       |
| `totals`             | All eleven absolute aggregate counters, not increments                   |
| `recentMinutes`      | Only changed aggregate minute buckets, with absolute counts              |
| `endpoints`          | Changed endpoint scalar summaries with only their changed minute buckets |
| `removedEndpointIds` | Stable IDs whose counters must be removed                                |
| `recentRequests`     | Complete bounded replacement feed, or `null` when unchanged              |

Clients discard minutes outside the window, initialize missing minutes to zero, and retain
unchanged buckets and endpoint summaries. A newly tracked endpoint therefore needs only its
nonzero buckets. The request feed is replaced atomically, including an empty replacement:
logical retries can update an existing summary rather than append a new request.

Successful overall or endpoint resets, reconnects, and clients missing an intermediate server
frame receive a fresh full baseline. Clamped duplicate UTC minute identities also require a
baseline. SSE IDs guard continuity; they do not enable `Last-Event-ID` replay. The dashboard
ignores duplicate sequences and closes an unusable stream before recovering at the normal
15-second polling interval. It keeps its stream baseline separate from immediate HTTP refreshes,
and retiring a broken stream does not abort an authoritative management refresh.

This reduces repeated serialization, wire traffic, and browser parsing, not the initial baseline
or full snapshot capture. Local managed Release tests on Windows ARM64/.NET 10.0.13, with
**256 tracked endpoints and 100 recent summaries**, measured **2,811,112 bytes** for a full
snapshot versus **21,938 bytes** for one changed endpoint and the replacement feed:
**99.22% smaller**. The regression test requires a sparse interval to use less than 2% of the full
payload. Busy intervals touching every endpoint can be larger; these are not throughput,
container-memory, or universal compression guarantees.

Endpoint definitions stay only in page memory, never local storage. Administrative JSON and
dashboard HTML use `Cache-Control: no-store`. The HTML references scripts, styles, and images with
one SHA-256 version derived from the complete dashboard asset set. Matching versioned URLs use
`private, max-age=31536000, immutable`; module imports carry the same version. Unversioned or stale
asset URLs use `private, no-cache` with content ETags for compatibility and revalidation. Shared
proxies must not cache authenticated dashboard assets.

Statistics-only updates preserve endpoint row controls and focus unless the selected sort changes
row order. Collapsed panels and inactive statistics views are rendered when shown.
The Statistics table retains rows by stable endpoint ID or aggregate measure and changes only
different cell text. The graph retains UTC-keyed columns, their six status segments, and one tooltip.
It replaces a column only when its button/non-button behavior changes, clears listeners on removed
or replaced columns, and bounds the cache to the current minute window. Surviving focused columns,
manual horizontal scroll, and follow-newest behavior are preserved; removed focus returns to the
timeline. Endpoint switching still uses complete histories already held in page memory.
The Endpoints table offers an icon-only chevron before the Path header, vertically aligned with
the API-group chevrons. It is separate from Path sorting, with an accessible name and tooltip of
**Collapse all groups**, switching to **Expand all groups** when all API
groups in the current filtered result are collapsed. With a mixture of expanded and collapsed groups,
the action collapses them all. Groups hidden by filters retain their state; new API groups start
expanded. The control is disabled when there are no matching groups. Individual group controls remain
available, and endpoint selection, sorting, configuration, and persistence are unchanged.
Settings defaults **Remember expanded/collapsed API groups for this session** to enabled.
Group collapse state survives refreshes in the same tab using `sessionStorage`, not `localStorage`
or server configuration. The on/off preference itself is browser-local. Turning it off clears the
saved group state without expanding the current view; refreshes then start all groups expanded.
New tab sessions start expanded. The existing whole-panel collapse preference remains independent.

The request log offers **Collapse all buckets**, switching to **Expand all buckets** when all
current buckets are collapsed. New minute buckets start expanded; additional requests in an
existing bucket retain its state. Table columns use stable widths and reserved scrollbar space,
with long values wrapping and horizontal scrolling available on narrow panels.

Endpoint rows use separate duplication and clipboard icons. A fifth **Copy cURL command** action
copies the first configured method, current instance origin, and the same illustrative JSON payload
as the test blade for POST/PUT/PATCH. HEAD uses `--head`. Commands use `curl.exe` and PowerShell 7
quoting on Windows, or `curl` and POSIX shell quoting on macOS/Linux. Protected requests reference
the caller's `MOCKAPI_KEY` environment variable rather than copying the dashboard key. Set it before
running the command (`$env:MOCKAPI_KEY = '<your-key>'` in PowerShell, or
`export MOCKAPI_KEY='<your-key>'` in bash). Settings > Workspace preferences can hide or restore
this action; it defaults to visible and is saved only in this browser. Clipboard failures are
reported in the dashboard.

### Statistics Allocation Diagnostic

The opt-in [statistics delivery diagnostic](../tests/MockAPI.Tests/Management/StatisticsDeliveryDiagnostics.cs)
measures the actual GET and SSE handlers against synchronous non-retaining sinks, plus an
uncached snapshot/serialization reference. Normal regression tests separately pin exact GET
and SSE bytes, cache invalidation, refreshes, concurrent recording/readers/resets, cancellation,
and warmed allocation bounds.

On 2026-10-21, local Windows ARM64 managed Release runs on .NET 10.0.13 used **256 tracked
endpoints**, **100 recent summaries**, and **2,811,113 compact JSON characters**. Tiered
compilation was disabled. Each allocation result below is the median of five samples of ten
calls after ten warmups; fixture creation is outside measurement.

| Idle controlling path                  | Allocated bytes/call before | Allocated bytes/call after |
| -------------------------------------- | --------------------------- | -------------------------- |
| GET statistics, indented serialization | 2,301,002                   | 1,265,032                  |
| Standalone SSE, one statistics event   | 9,650,524                   | 1,317,408                  |
| Dashboard SSE, initial two events      | 8,794,341                   | 1,454,212                  |
| Dashboard compact JSON cache lookup    | 0                           | 0                          |

GET still allocates serializer traversal state; it does not cache a second indented payload.
The shared cache retains approximately 1 MiB of snapshot data at this fixture size in addition
to the compact string. Changed versions still require snapshot capture and serialization;
no improvement is claimed for continuously changing, single-consumer traffic. Array-pool and
GC behavior affected individual samples, especially full serialization and framing, so these
figures describe this local experiment, not HTTP throughput, network latency, container memory,
or cross-environment guarantees. The diagnostic also reports elapsed-time samples.

Repeat against the same fixture, runtime, build configuration, and environment for both revisions:

```powershell
pwsh -NoProfile -Command {
    $env:MOCKAPI_STATISTICS_DIAGNOSTICS = '1'
    $env:DOTNET_TieredCompilation = '0'
    dotnet test .\tests\MockAPI.Tests\MockAPI.Tests.csproj --no-restore `
        --configuration Release --filter "FullyQualifiedName~StatisticsDeliveryDiagnostics" `
        --logger "console;verbosity=detailed"
    exit $LASTEXITCODE
}
```

```bash
MOCKAPI_STATISTICS_DIAGNOSTICS=1 DOTNET_TieredCompilation=0 \
  dotnet test ./tests/MockAPI.Tests/MockAPI.Tests.csproj --no-restore \
    --configuration Release --filter "FullyQualifiedName~StatisticsDeliveryDiagnostics" \
    --logger "console;verbosity=detailed"
```

### Recording Contention Diagnostic

The opt-in [recording diagnostic](../tests/MockAPI.Tests/Runtime/StatisticsRecordingDiagnostics.cs)
runs five fixed-work samples of 120,000 requests with 1, 2, 4, or 8 dedicated workers. Run it
alone because `Monitor.LockContentionCount` is process-wide. A local Windows ARM64 managed Release
run on .NET 10.0.13, with tiered compilation disabled, produced these medians:

| Workers | Matched requests/s with bounded feed | Unmatched requests/s without logical ID | Matched monitor contentions/sample |
| ------- | ------------------------------------ | --------------------------------------- | ---------------------------------- |
| 1       | 2.59 million                         | 6.41 million                            | 0                                  |
| 2       | 2.85 million                         | 5.70 million                            | 18                                 |
| 4       | 2.96 million                         | 6.00 million                            | 45                                 |
| 8       | 2.57 million                         | 5.70 million                            | 53                                 |

These paths do different work. Monitor counts do not attribute contention to one gate and do not
measure spinning, cache-line traffic, or HTTP throughput. The probe does not isolate a bottleneck
that justifies sharding counters or replacing synchronization, so recording locks remain unchanged.
Use attributable profiling under a representative deployment load before such a redesign.

```powershell
pwsh -NoProfile -Command {
    $env:MOCKAPI_RECORDING_DIAGNOSTICS = '1'
    $env:DOTNET_TieredCompilation = '0'
    dotnet test .\tests\MockAPI.Tests\MockAPI.Tests.csproj --no-restore `
        --configuration Release --filter "FullyQualifiedName~StatisticsRecordingDiagnostics" `
        --logger "console;verbosity=detailed"
    exit $LASTEXITCODE
}
```

```bash
MOCKAPI_RECORDING_DIAGNOSTICS=1 DOTNET_TieredCompilation=0 \
  dotnet test ./tests/MockAPI.Tests/MockAPI.Tests.csproj --no-restore \
    --configuration Release --filter "FullyQualifiedName~StatisticsRecordingDiagnostics" \
    --logger "console;verbosity=detailed"
```

### Backend Allocation Diagnostic

The opt-in [backend diagnostic](../tests/MockAPI.Tests/Runtime/BackendRefactoringDiagnostics.cs)
measures repeated unchanged configuration replacement, population of all 256 tracked endpoint
counters, and valid and invalid API-key verification. Regression tests separately enforce compiled
body/header reuse, independent limiter permits, bounded population allocations, and allocation-free
warmed key verification with minimum, generated-length, and maximum-length keys.

Local Windows ARM64 managed Release measurements on .NET 10.0.13, with tiered compilation disabled,
produced these medians of five samples after ten warmups:

| Operation                                              | Allocated bytes/call before | Allocated bytes/call after |
| ------------------------------------------------------ | --------------------------- | -------------------------- |
| Full replacement, four unchanged 960 KiB bodies        | 10,394,519                  | 6,045,249                  |
| Populate 256 endpoint counters                         | 3,513,888                   | 858,784                    |
| Verify a valid 43-character key, existing HTTP context | 216                         | 0                          |
| Verify an invalid 43-character key, existing context   | 216                         | 0                          |

Configuration and population samples contain ten operations; authentication samples contain 1,000.
Fixture documents, endpoint IDs, keys, and HTTP contexts are created outside measurement. Full
configuration replacement still clones, validates, and serializes the complete document. Serializer
pooling and GC introduce allocation variation; response-data reuse saves body/header recompilation,
not those required document operations. The stable-ID index adds bounded per-snapshot bookkeeping,
and authentication retains one decoded digest alongside the persisted hexadecimal representation.

These are allocation measurements, not retained-heap, RSS, contention, HTTP-throughput, or Native AOT
results. Counter synchronization is unchanged; do not infer parallel throughput gains from the
smaller object graph. The diagnostic also reports elapsed-time samples.

```powershell
pwsh -NoProfile -Command {
    $env:MOCKAPI_BACKEND_DIAGNOSTICS = '1'
    $env:DOTNET_TieredCompilation = '0'
    dotnet test .\tests\MockAPI.Tests\MockAPI.Tests.csproj --no-restore `
        --configuration Release --filter "FullyQualifiedName~BackendRefactoringDiagnostics" `
        --logger "console;verbosity=detailed"
    exit $LASTEXITCODE
}
```

```bash
MOCKAPI_BACKEND_DIAGNOSTICS=1 DOTNET_TieredCompilation=0 \
  dotnet test ./tests/MockAPI.Tests/MockAPI.Tests.csproj --no-restore \
    --configuration Release --filter "FullyQualifiedName~BackendRefactoringDiagnostics" \
    --logger "console;verbosity=detailed"
```

## Configuration Read Efficiency

Configuration status reads and SSE configuration-event projections use an owned, immutable
API-description projection from the captured snapshot. They do not deserialize endpoint definitions
or response bodies just to retrieve metadata. Persistence-status-only updates share this projection
while keeping descriptions, revision, and ETag consistent.

Single-endpoint mutation responses scan the captured canonical JSON and deserialize only the
selected endpoint, skipping unrelated endpoint bodies. They still return detached, canonical data
from the applied revision, and complete candidate validation and atomic publication are unchanged.
The opt-in backend allocation diagnostic compares this with materializing the entire document.
With four unrelated 960 KiB bodies and one small selected endpoint, five ten-call managed Release
samples measured a median **7,869,780 bytes/read** for the full-document path versus
**1,343 bytes/read** for targeted extraction. The scan remains proportional to the bytes preceding
the selected endpoint; this does not make configuration writes allocation-free.

Unfiltered native exports, including requests with omitted or empty endpoint IDs, use the snapshot's
canonical serialized bytes without first materializing a configuration document. This removes a
redundant parse and object graph, not the allocation needed to return detached export bytes. Selected
native exports and generated formats retain their filtering and transformation paths.

These changes reduce temporary allocations and read-side processing for configurations with large
response bodies. They preserve exact unfiltered native-export bytes, detached documents, null versus
empty metadata maps, description whitespace, case-sensitive keys, and ordinal key ordering.

### Measured Results

On 2026-10-14, before/after diagnostics used a validated document with **25 endpoints and 4,163,270
bytes**, or **99.26% of the document limit**. The local managed Release build ran on .NET 10.0.13
with tiered compilation disabled. Each result is the median of five samples of 30 calls after
10 warmup calls.

| Controlling path                    | Allocated bytes/call before | Allocated bytes/call after | Milliseconds/call before | Milliseconds/call after |
| ----------------------------------- | --------------------------- | -------------------------- | ------------------------ | ----------------------- |
| Status projection, also used by SSE | 8,336,233                   | 48                         | 5.149143                 | 0.000067                |
| Status handler                      | 8,340,895                   | 746                        | 4.620420                 | 0.003077                |
| Native export, omitted endpoint IDs | 12,499,727                  | 4,163,869                  | 6.550490                 | 0.546547                |
| Native export, empty endpoint IDs   | 12,499,403                  | 4,163,873                  | 6.303837                 | 0.479567                |

The handler measurements invoke the actual status/export handlers with a `Stream.Null` response
sink. The diagnostic measures thread-local allocations and elapsed time, and verifies synchronous
completion. Fixture creation is outside the measured calls. These results demonstrate improvements
in the controlling read paths, not end-to-end HTTP throughput, SSE delivery latency, container
memory use, or guarantees for other environments. The SSE projection measurement does not include
event serialization or network delivery.

### Repeating the Diagnostic

The opt-in [configuration-read diagnostic](../tests/MockAPI.Tests/Management/ConfigurationReadDiagnostics.cs)
reports allocation and elapsed-time samples without imposing timing thresholds on normal tests.
Run it from the repository root against the current implementation. To compare revisions, use the
same diagnostic fixture, runtime, build configuration, and environment for both versions.

PowerShell uses a child process so the diagnostic environment settings do not persist in your shell:

```powershell
pwsh -NoProfile -Command {
    $env:MOCKAPI_CONFIGURATION_READ_DIAGNOSTICS = '1'
    $env:DOTNET_TieredCompilation = '0'
    dotnet test .\tests\MockAPI.Tests\MockAPI.Tests.csproj --no-restore `
        --configuration Release --filter "FullyQualifiedName~ConfigurationReadDiagnostics" `
        --logger "console;verbosity=detailed"
    exit $LASTEXITCODE
}
```

```bash
MOCKAPI_CONFIGURATION_READ_DIAGNOSTICS=1 DOTNET_TieredCompilation=0 \
  dotnet test ./tests/MockAPI.Tests/MockAPI.Tests.csproj --no-restore \
    --configuration Release --filter "FullyQualifiedName~ConfigurationReadDiagnostics" \
    --logger "console;verbosity=detailed"
```

## Built-In Merge Results

The example combines [Trailhead AOAI](../config/trailhead-aoai.json),
[Trailhead inventory](../config/trailhead-inventory.json), and
[Trailhead shipping](../config/trailhead-shipping.json): 41 definitions across three API groups.
It does not automatically replace saved configurations. There is no endpoint-count limit for
configuration or bulk selections. The complete candidate must still fit the 4 MiB UTF-8 document-size
limit; an oversized merge returns `422` without changing the active configuration, even when forced.
Bulk selections must be non-empty, unique, and reference existing endpoints; management request bodies
remain limited to 4 MiB.

A successful merge reports `added`, `updated`, `skipped`, the resulting revision, and whether a change was applied. Repeating the same merge is an idempotent `200` response with no revision increment.

Missing API descriptions are added atomically with the endpoints. Existing custom or empty descriptions
are preserved even with `force=true`. A metadata-only addition reports `applied: true` and advances the
revision without increasing the endpoint `added` or `updated` counts.

A divergent stable ID or a method/path collision returns `409` with a conflict list and no mutations. The dashboard shows this preview before it offers **Force update**. Forced updates replace only the reviewed built-in identities or colliding routes, add missing built-ins, and preserve unrelated endpoints.

## Errors and OpenAPI

Validation, conflict, and persistence failures use RFC 9457-style problem details. Validation errors include a path, code, and actionable message and never expose filesystem details.

MockAPI exposes two distinct OpenAPI documents:

| Document             | Route                                         | Contents                                                      | Required switches                                                     |
| -------------------- | --------------------------------------------- | ------------------------------------------------------------- | --------------------------------------------------------------------- |
| Management API       | `/__mockapi/openapi/v1.json`                  | Reserved administrative operations, not runtime-defined mocks | `MockApi__EnableManagementApi=true` and `MockApi__EnableOpenApi=true` |
| Live mock API export | `/__mockapi/api/configuration/export/openapi` | Currently enabled mock endpoints and configured responses     | `MockApi__EnableManagementApi=true`                                   |

Swagger UI at `/__mockapi/swagger` describes the management API. Use the live mock export for APIM backend import, not the management specification. Both documents are administrative surfaces and require Basic authentication when dashboard credentials are configured.

## Live Mock OpenAPI and APIM Import

### Download the current definition

- Local container: [live mock OpenAPI JSON](http://localhost:8080/__mockapi/api/configuration/export/openapi).
- Local .NET: [live mock OpenAPI JSON](http://localhost:5080/__mockapi/api/configuration/export/openapi).
- Deployed instance: `https://<mockapi-host>/__mockapi/api/configuration/export/openapi`.
- Dashboard: select the **OpenAPI logo** beside the **Endpoints** heading to view the JSON in a new browser tab. Hover text identifies OpenAPI 3.1.0 and explains that the document updates automatically; the accessible name also announces the new tab. The link uses the current instance's origin with `?download=false`, so it works locally and after deployment. To download a file instead, choose **OpenAPI** in the export-format selector, then select **Export**.

The dashboard bundles the official color symbol from the [OpenAPI Initiative style guide](https://github.com/OAI/OpenAPI-Style-Guide/tree/66757810240276e60703c67b09e4cac42ede1c59). The source artwork is unchanged; an SVG view fragment displays the symbol without lettering, as permitted by the guide. Its [Apache 2.0 license](../src/MockAPI/wwwroot/openapi-logo.LICENSE.txt) is distributed with the asset. No external image request is needed, and use of the logo identifies the specification rather than implying OpenAPI Initiative endorsement.

The response is OpenAPI **3.1.0** JSON, generated on each request from one immutable active configuration snapshot. By default, it downloads as an attachment named `mockapi.openapi.json`. Append `?download=false` to view it inline in the browser; `?download=true` explicitly requests an attachment. The optional `download` query parameter is supported by the portable-format export route. Portable exports send `Cache-Control: no-store`, and their ETag identifies the configuration revision. This is not a static file generated during build or deployment. Refresh an open JSON tab to retrieve subsequent changes.

Applied runtime changes are reflected in the next export, including changes whose automatic save failed.
Disabling or deleting an endpoint removes its operations; enabling an endpoint adds them. Failed validation
or stale writes leave both the active endpoints and their exported definition unchanged. Automatic saving
controls persistence across restarts, not OpenAPI generation. An export already in flight can represent the
preceding snapshot when a concurrent edit completes.

Only enabled endpoints appear. Operations have stable IDs derived from the endpoint ID and method. The export includes configured response statuses and body examples, including both success and `429` outcomes for rate-limited endpoints. Management and health routes are not included.
The exported `info.version` comes from MockAPI's assembly application version; it is independent of
the OpenAPI protocol version and configuration `schemaVersion`.

The `info.version` field is not a configuration revision. Use the response ETag to detect configuration changes, fetch a fresh export before importing, and avoid configuring a proxy cache in front of this administrative route. Previously downloaded files do not update themselves.

### Import into APIM

APIM supports [OpenAPI import from a URL or file](https://learn.microsoft.com/azure/api-management/import-api-from-oas). Provision APIM separately; the repository's Azure deployment provisions MockAPI and its supporting resources, not an APIM instance.

In the Azure portal:

1. Open the APIM instance, then **APIs > Add API > OpenAPI**.
2. Supply the deployed live mock export URL if it is reachable by the import service without administrative credentials. A localhost URL cannot reach your development machine from Azure.
3. For authenticated or private deployments, download the document from a trusted machine with access and upload the file instead. Keep Basic credentials out of URLs, source control, and command history; do not disable administrative authentication for import.
4. Set **Web service URL** to `https://<mockapi-host>`, without the export route or APIM API suffix. The generated document currently uses `http://localhost:8080` in `servers`; APIM requires the actual backend URL to be supplied explicitly.
5. Choose an API URL suffix such as `mockapi`, create the API, and verify its operations and backend setting.
6. Call an imported operation through the gateway, for example `https://<apim-gateway>/mockapi/inventory/items`. Supply a subscription key if required by APIM. The gateway must have network access to the MockAPI backend independently of how the specification was imported.

For a protected export, these examples prompt for credentials rather than putting the password in the command. Replace the host placeholder and keep the downloaded artifact outside source control.

PowerShell 7:

```powershell
$credential = Get-Credential
Invoke-WebRequest `
    -Uri 'https://<mockapi-host>/__mockapi/api/configuration/export/openapi' `
    -Authentication Basic -Credential $credential `
    -OutFile mockapi.openapi.json
```

Bash:

```bash
read -r -p 'MockAPI dashboard username: ' MOCKAPI_USERNAME
curl --fail --show-error --user "$MOCKAPI_USERNAME" \
  'https://<mockapi-host>/__mockapi/api/configuration/export/openapi' \
  --output mockapi.openapi.json
```

After signing in with Azure CLI, import the downloaded file. The following single-line command works in both PowerShell and Bash; replace each placeholder:

```azurecli
az apim api import --resource-group "<apim-resource-group>" --service-name "<apim-service-name>" --api-id mockapi --path mockapi --specification-format OpenApiJson --specification-path mockapi.openapi.json --service-url "https://<mockapi-host>"
```

For an accessible, unauthenticated definition, replace `--specification-path mockapi.openapi.json` with `--specification-url "https://<mockapi-host>/__mockapi/api/configuration/export/openapi"`. Keep `--service-url` in either case.

### Keep APIM current

**The source document stays current with MockAPI; APIM does not automatically stay current with the source document.** An OpenAPI URL import is a one-time read, not a subscription.

- Changes to the response of an already imported route take effect when APIM next forwards a request to MockAPI, unless APIM caching or policies override the result. No re-import is needed just to receive that new response.
- Added, removed, disabled, or changed method/path operations require re-import to align APIM's operation catalog. Re-import also refreshes its response documentation.
- Fetch a fresh export and repeat the import with the same `--api-id` to update rather than create another API. Preserve MockAPI endpoint IDs for stable operation identity.
- Review changes before re-import: APIM can delete operations absent from the new document, including disabled mock routes. Check operation policies and use an APIM revision for a staged update when appropriate.
- For automated synchronization, an external job must detect a changed configuration ETag, download the corresponding export, and deliberately update APIM. MockAPI does not currently provide this job or push changes to APIM. A response-only change can still warrant re-import for accurate documentation.

### Compatibility and behavior limits

APIM accepts OpenAPI 3.1 for import, but Microsoft documents it as [import-compatible, not fully feature-compatible](https://learn.microsoft.com/azure/api-management/api-management-api-import-restrictions). Validate the generated document and test the imported API rather than assuming every legal MockAPI configuration maps to APIM.

- Use OpenAPI-supported HTTP methods for imported operations (`GET`, `PUT`, `POST`, `DELETE`, `OPTIONS`, `HEAD`, `PATCH`, `TRACE`). MockAPI also accepts custom HTTP tokens that cannot be represented as standard OpenAPI 3.1 operations.
- MockAPI paths are literal, not templates. A literal path containing braces must not be mistaken for an OpenAPI path parameter. APIM also has URL-template restrictions that can be tighter than MockAPI's path limits.
- Body examples describe raw configured text, not inferred JSON object schemas. The export is not a full request/response validation contract and does not describe every configured response header.
- Connection aborts are described with an extension and a default response description. APIM ignores custom extensions; it does not recreate disconnects or MockAPI's rate-limit state from the specification.
- Do not enable APIM's mock-response policy if the intent is to exercise the real MockAPI backend. Gateway retries, circuit breakers, caching, and error normalization can change the caller-visible outcome, especially for intentional disconnects.
- Specification import does not configure APIM products, caller authorization, backend pools, retry policies, or automatic refresh. Review those separately for the test scenario.
