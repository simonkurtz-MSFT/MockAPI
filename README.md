# MockAPI

Project planning is tracked in [docs/PLAN.md](docs/PLAN.md). Local container development uses WSLC; see [docs/WSLC.md](docs/WSLC.md) for verified commands, supported checks, and the CI boundary for multi-architecture releases.

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
```

Malformed, oversized, or semantically invalid configured files fail startup without activating a partial configuration. Runtime edits remain in memory until an explicit save operation atomically replaces the configured file; the management operation that invokes save is part of the next implementation slice.
