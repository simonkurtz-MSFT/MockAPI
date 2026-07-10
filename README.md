# MockAPI

Project planning is tracked in [docs/PLAN.md](docs/PLAN.md). Local container development uses WSLC; see [docs/WSLC.md](docs/WSLC.md) for verified commands, supported checks, and the CI boundary for multi-architecture releases.

## Small Container Footprint

The current native `linux/arm64` development image is only **26.84 MB** as reported by WSLC, with an estimated **13.85 MB gzip-compressed layer payload**. The self-contained, fully trimmed application runs in the minimal .NET runtime-deps image without carrying the SDK or shared .NET framework runtime. See the [measured image footprint](docs/WSLC.md#measured-image-footprint) for the exact baseline and methodology.

## Developer CLI

Run the root developer CLI interactively:

```powershell
.\start.ps1
```

Common automation-friendly actions:

```powershell
.\start.ps1 -Action setup
.\start.ps1 -Action test
.\start.ps1 -Action coverage
.\start.ps1 -Action validate
.\start.ps1 -Action container-build
.\start.ps1 -Action container-run
.\start.ps1 -Action container-test
```

`setup` checks the pinned .NET SDK and WSLC, then restores NuGet packages. Pass `-InstallMissing` to explicitly permit .NET installation through winget or a WSL update when a prerequisite is missing. Run `.\start.ps1 -Action help` for every action and option.

The WSLC container workflow builds the native host architecture, publishes port `8080`, applies the `0.5` CPU and `256 MiB` limits, and mounts the persistent `mockapi-data` volume at `/data`. `container-run` creates the container when absent and restarts it when stopped. Multi-platform image assembly and the checks unsupported by WSLC remain CI responsibilities.

## Configuration File

MockAPI loads and validates its configuration before serving requests. In containers, the default path is `/data/mockapi.json`; in local Development it is `mockapi.json` under the application content root. A missing file starts with an empty configuration by default.

Set these environment variables to override that behavior:

```text
MockApi__ConfigurationPath=/data/mockapi.json
MockApi__AllowEmptyConfiguration=false
MockApi__EnableManagementApi=true
```

Malformed, oversized, or semantically invalid configured files fail startup without activating a partial configuration. Runtime edits remain in memory until an explicit save operation atomically replaces the configured file; the management operation that invokes save is part of the next implementation slice.

## Management API

Endpoint management is available under `/__mockapi/api/endpoints`, with configuration revision and unsaved status at `/__mockapi/api/configuration`. Create, replace, enable/disable, and delete requests require the latest quoted ETag in `If-Match`; stale writes return HTTP `412` without changing the active configuration. Successful changes are immediately visible to the mock dispatcher and remain unsaved until an explicit save operation is invoked.

The initial management API has no authentication. Keep it on localhost or a protected network, or set `MockApi__EnableManagementApi=false` while leaving configured mock routes available.
