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

The management API is rooted at `/__mockapi/api`. When `MockApi__DashboardUsername` and `MockApi__DashboardPasswordHash` are configured together, HTTP Basic authentication protects it with the other administrative surfaces. Without that pair, keep it on localhost or a protected network unless unauthenticated public access is an explicit deployment choice.

## Concurrency

Every configuration write requires the latest strong ETag in `If-Match`, for example:

```http
If-Match: "4"
```

Read `/configuration` to obtain the current revision and ETag. A missing precondition returns `428`; a stale revision returns `412`. Validation and revision failures leave the active snapshot unchanged.

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
| `POST`                 | `/configuration/save`                     | Persist the active revision                          |
| `GET`, `POST`          | `/endpoints`                              | List or create endpoint definitions                  |
| `POST`                 | `/endpoints/bulk`                         | Atomically enable, disable, or delete endpoints      |
| `GET`, `PUT`, `DELETE` | `/endpoints/{id}`                         | Read, replace, or delete one endpoint                |
| `PUT`                  | `/endpoints/{id}/enabled`                 | Enable or disable one endpoint                       |
| `GET`                  | `/statistics`                             | Statistics and recent request summaries              |
| `GET`                  | `/statistics/events`                      | Server-Sent Events statistics and request stream     |
| `GET`                  | `/dashboard/events`                       | Configuration changes and coalesced statistics       |
| `POST`                 | `/statistics/reset`                       | Reset all process-local statistics                   |
| `POST`                 | `/statistics/endpoints/{id}/reset`        | Reset one endpoint's statistics                      |

Portable export formats are `postman`, `insomnia`, `curl`, `jmeter`, `openapi`, `k6`, and `http`. Only enabled endpoints are included. The generated artifacts use `http://localhost:8080` as their editable default base URL. Postman, JMeter, and k6 exports include assertions for configured responses; cURL, Insomnia, and HTTP exports provide runnable requests; OpenAPI describes the configured responses.

API description edits accept `{"path":"/ex","description":"API overview"}` and require the
editor's captured strong `If-Match` ETag. Both properties are required strings; an empty description
clears its displayed text. Success returns `200` with the complete configuration status, including
`apiDescriptions`. Invalid metadata returns `422` without changing any endpoints or descriptions.
The configuration status and SSE configuration events carry metadata from the same revision.
See [API descriptions](CONFIGURATION.md#api-descriptions) for identity, limits, and persistence rules.

Native export preserves all API metadata. OpenAPI exports represent descriptions as top-level tags,
with matching enabled operations assigned to those tags; individual operation descriptions remain
separate. Other portable request/test formats remain endpoint-focused and omit API-level metadata.

Bulk endpoint requests provide one or more unique stable endpoint IDs and an `operation` of `enable`, `disable`, or `delete`. Every ID must exist. The complete operation uses one configuration revision and leaves the active snapshot unchanged when validation, endpoint lookup, or ETag checks fail.

Statistics counters record every transport attempt seen by the mock dispatcher. For wire compatibility, fields such as `totalRequests`, `matchedRequests`, `unmatchedRequests`, and `recentMinutes[].requests` retain their existing names but contain transport-attempt counts. Statistics snapshots include at most 100 recent logical request summaries for configured endpoints in newest-first order; unmatched requests remain in aggregate statistics but are excluded from the request log. Dashboard-generated retries with the same request ID are coalesced into one summary whose `transportAttempts` value reports the physical dispatch count. Each summary contains the timestamp, method, normalized path, matched endpoint ID, outcome, HTTP status when available, and completed response byte count. The process never retains request or response bodies, headers, cookies, authorization values, or query values. Resetting all statistics clears the feed; resetting one endpoint removes its summaries.

## Dashboard Synchronization and Caching

The dashboard opens one authenticated `GET /dashboard/events` connection. It receives a
`configuration` event containing the same JSON shape as `/configuration`, followed by a
`statistics` event containing the same shape as `/statistics`. Configuration events are
triggered by atomic snapshot publication, including saves that clear the dirty flag without
incrementing the revision. Invalid or conflicting writes do not publish events.

Statistics are coalesced to at most one update every two seconds and serialized once per
changed snapshot across connected dashboards. Idle statistics are not retransmitted, except
at minute boundaries to advance the rolling window. Idle streams send keepalive comments
approximately every 16 seconds. Disable proxy buffering and allow long-lived SSE responses.
The existing `/statistics/events` route remains available with its original behavior.

Reconnects receive current state; there is no replay queue or `Last-Event-ID` history. The
dashboard reloads endpoint definitions only when their revision changes or after reconnecting
(a restart can reuse a numeric revision). Saves at the same revision update the Save button
without reloading endpoints. Unsupported or failed SSE uses a 15-second completion-scheduled
poll, which stops after stream recovery. Hidden pages close the stream and abort requests;
returning pages reconnect. Explicit mutations still refresh immediately.

Endpoint definitions stay only in page memory, never local storage. Administrative JSON and
dashboard HTML use `Cache-Control: no-store`. The HTML references scripts, styles, and images with
one SHA-256 version derived from the complete dashboard asset set. Matching versioned URLs use
`private, max-age=31536000, immutable`; module imports carry the same version. Unversioned or stale
asset URLs use `private, no-cache` with content ETags for compatibility and revalidation. Shared
proxies must not cache authenticated dashboard assets.

Statistics-only updates preserve endpoint row controls and focus unless the selected sort changes
row order. Collapsed panels and inactive statistics views are rendered when shown.
The request log offers **Collapse all buckets**, switching to **Expand all buckets** when all
current buckets are collapsed. New minute buckets start expanded; additional requests in an
existing bucket retain its state. Table columns use stable widths and reserved scrollbar space,
with long values wrapping and horizontal scrolling available on narrow panels.

## Built-In Merge Results

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

Successful runtime changes are reflected in the next export, including unsaved changes. Disabling or deleting an endpoint removes its operations; enabling an endpoint adds them. Failed validation or stale writes leave both the active endpoints and their exported definition unchanged. **Save** controls persistence across restarts, not OpenAPI generation. An export already in flight can represent the preceding snapshot when a concurrent edit completes.

Only enabled endpoints appear. Operations have stable IDs derived from the endpoint ID and method. The export includes configured response statuses and body examples, including both success and `429` outcomes for rate-limited endpoints. Management and health routes are not included.

The `info.version` field is not a configuration revision. Use the response ETag to detect configuration changes, fetch a fresh export before importing, and avoid configuring a proxy cache in front of this administrative route. Previously downloaded files do not update themselves.

### Import into APIM

APIM supports [OpenAPI import from a URL or file](https://learn.microsoft.com/azure/api-management/import-api-from-oas). Provision APIM separately; the repository's Azure deployment provisions MockAPI and its supporting resources, not an APIM instance.

In the Azure portal:

1. Open the APIM instance, then **APIs > Add API > OpenAPI**.
2. Supply the deployed live mock export URL if it is reachable by the import service without administrative credentials. A localhost URL cannot reach your development machine from Azure.
3. For authenticated or private deployments, download the document from a trusted machine with access and upload the file instead. Keep Basic credentials out of URLs, source control, and command history; do not disable administrative authentication for import.
4. Set **Web service URL** to `https://<mockapi-host>`, without the export route or APIM API suffix. The generated document currently uses `http://localhost:8080` in `servers`; APIM requires the actual backend URL to be supplied explicitly.
5. Choose an API URL suffix such as `mockapi`, create the API, and verify its operations and backend setting.
6. Call an imported operation through the gateway, for example `https://<apim-gateway>/mockapi/ex/hello`. Supply a subscription key if required by APIM. The gateway must have network access to the MockAPI backend independently of how the specification was imported.

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
