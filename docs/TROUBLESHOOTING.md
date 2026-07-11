# Troubleshooting

## Dashboard Is Unavailable

Verify `/health/ready` first. If health succeeds but `/` returns `404`, ensure `MockApi__EnableDashboard=true`. The dashboard also needs `MockApi__EnableManagementApi=true` for data and editing operations.

## Configuration Does Not Survive Restart

Runtime changes are not automatically persisted. Select **Save** and confirm the dashboard no longer reports unsaved changes. Containers must mount a writable volume at `/data`; ephemeral container storage is lost during recreation.

## A Write Returns 412 or 428

`428 Precondition Required` means the request omitted `If-Match`. `412 Precondition Failed` means another write advanced the revision. Refresh `/configuration`, review the new active state, and retry with its ETag.

## Built-In Examples Are Not Added

An identical example is skipped, so repeated loading may correctly report no changes. A changed stable ID or method/path collision returns a conflict preview and makes no changes. Review the named endpoints and choose **Force update** only when the checked-in built-in version should win. Unrelated endpoints are preserved.

## Endpoint Returns 404

Check that the endpoint is enabled and that the HTTP method and path match exactly. Paths are case-sensitive; query strings do not affect matching. Runtime-defined routes cannot use `/`, `/__mockapi`, `/health`, or application-owned descendants.

## Reason Phrase Is Missing

HTTP reason phrases are transmitted only by HTTP/1.1. HTTP/2 and HTTP/3 omit them by protocol design. The configured response body is independent of the reason phrase.

## WSLC Container Problems

```powershell
.\start.ps1 -Action container-status
.\start.ps1 -Action container-logs
.\start.ps1 -Action container-test
```

The local container is recreated when the `mockapi:dev` image ID changes. The named data volume is retained. WSLC `2.9.3.0` may warn that swap cannot be limited separately; the configured memory limit remains active.

The example showcase intentionally fails when `/ex/rate-limited` is not loaded. Load examples from the dashboard and rerun:

```powershell
.\start.ps1 -Action container-showcase
```