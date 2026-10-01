# Container Development And Validation

MockAPI uses one Native AOT image per platform and one Dockerfile across local development, CI, Docker Compose, and Azure Container Apps. A single multi-platform OCI index selects between the AMD64 and ARM64 images. The [WSL container CLI (`wslc`)](https://learn.microsoft.com/windows/wsl/wsl-container) is the default local engine; Docker is also supported explicitly through the developer CLI. The original WSLC capability baseline below used `2.9.3.0`; native AOT validation on 2026-09-25 used `2.9.13.0` on a Windows ARM64 host.

This guide covers the shared image contract, local container workflow, engine-specific constraints, measured image footprint, platform support, and release-validation boundary. See Microsoft's [WSL container tutorial](https://learn.microsoft.com/windows/wsl/tutorials/wsl-containers) for WSLC installation and introductory usage. Run `wslc version` and review command help before relying on these constraints with a newer release.

For the version-bump, automatic-tag, approval, Docker Hub, and GitHub release procedure, use
[Versioning and Releases](RELEASING.md). Production publication is opt-in and always builds the exact
validated tag commit. The [development container](DEVELOPMENT.md) is for editing and testing, not distribution.

## WSLC Capabilities

| Capability                                      | WSLC 2.9.3.0        | MockAPI use                                               |
| ----------------------------------------------- | ------------------- | --------------------------------------------------------- |
| Build from a Dockerfile                         | Yes                 | Build the native ARM64 development image                  |
| Run and manage containers                       | Yes                 | Exercise health, management, dashboard, and mock routes   |
| Limit CPU and memory                            | Yes                 | Test with the `0.5` CPU and `256 MiB` release limits      |
| Named and bind-mounted volumes                  | Yes                 | Verify `/data` persistence across container recreation    |
| Networks and published ports                    | Yes                 | Expose port `8080` for host-side HTTP checks              |
| Image inspection, tagging, push, and pull       | Yes                 | Inspect metadata and publish architecture-specific images |
| Resource statistics                             | Yes                 | Record CPU, memory, I/O, and process observations         |
| Select a build, pull, or run platform           | No                  | Cannot request `linux/amd64` on this ARM64 host           |
| Create or inspect a multi-platform image index  | No                  | Assemble and verify the release index in CI               |
| Enforce a read-only root filesystem at run time | No exposed option   | Verify on a CI runner with suitable container tooling     |
| Generate an SBOM or scan vulnerabilities        | No built-in command | Perform both release checks in CI                         |

WSLC uses Dockerfiles as its image build format; using `wslc` does not require Docker Desktop or the Docker CLI.

## Local ARM64 Workflow

Use the developer CLI from the repository root for the normal workflow:

```powershell
.\start.ps1 -Action container-build
.\start.ps1 -Action container-run
.\start.ps1 -Action container-test
.\start.ps1 -Action container-showcase
```

The showcase action merges the built-in examples into the active configuration when the rate-limit example is
missing, preserving unrelated endpoints, and then verifies its HTTP response and statistics. For protected mocks,
set `MOCKAPI_KEY` to the key generated in dashboard Settings. When dashboard authentication is enabled, also set
the paired `MOCKAPI_DASHBOARD_USERNAME` and `MOCKAPI_DASHBOARD_PASSWORD` environment variables. Both developer
CLIs use those values without printing them; never put credentials in command history.

Native CI first verifies that the image's default settings reject mock calls with `401`. The isolated response
and persistence fixture then explicitly sets `MockApi__RequireApiKey=false`; this is a test-only opt-out, not a
deployment default.

The developer CLI gives each image a permanent UTC timestamp tag, then moves the `mockapi:dev` alias to the new image. The equivalent direct WSLC commands are:

```powershell
$buildImage = 'mockapi:build-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$nugetSource = (dotnet nuget list source --format Short | Select-String '^E\s+https://' | Select-Object -First 1).Line.Substring(2)
wslc build --pull --tag $buildImage --build-arg "NUGET_SOURCE=$nugetSource" .
wslc image tag $buildImage mockapi:dev
```

Because every build retains its timestamp tag, moving `mockapi:dev` does not leave the previous image untagged. On the first build after adopting this convention, the developer CLI preserves an existing alias-only image using its creation timestamp and short image ID before moving the alias.

The developer CLI uses the first enabled HTTPS source reported by `dotnet nuget list source --format Short` for the container restore. This supports machines that disable nuget.org in favor of an approved proxy feed. Use `-NuGetSource <url>` or `--nuget-source <url>` to override discovery. Only the source URL is passed to the build; host NuGet configuration and credentials are not copied into the image.

### Build connectivity failures

A browser opening an MCR artifact page proves that Windows can reach the catalog and that the tag exists. It does not prove that the WSLC Linux environment can reach the registry. When a build reports `dial tcp ... connect: network is unreachable`, isolate the engine path with the exact pinned images:

```powershell
wslc pull mcr.microsoft.com/dotnet/sdk:10.0.400-alpine3.24-aot@sha256:cd255a72d14d70260bb9b8dc493c3c37ce0877ef534f85ac1c931628364d7059
wslc pull mcr.microsoft.com/dotnet/runtime-deps:10.0.11-alpine3.24@sha256:379b17d7d388a2a1b5330bfc2429a01091f85e255d3bce7981d65927d786c000
```

If Windows can reach `mcr.microsoft.com:443` but either pull fails, the fault is in WSLC networking rather than the Dockerfile or image tag. Recreate the current user's independent WSLC session and retry the pulls:

```powershell
wslc system session terminate
```

WSLC sessions have their own lifecycle and can survive `wsl --shutdown`, so shutting down ordinary WSL alone may not refresh the failing network namespace. The next WSLC command recreates the default session automatically. If the pulls still fail in a fresh session, check VPN, proxy, firewall, and WSL networking policy.

WSLC may also print `git was not found in the system` while attempting to capture source metadata. That warning comes from the builder environment even when Git is available on the Windows `PATH`; it is non-fatal and is not the cause of a later registry, Dockerfile, or publish failure. The developer CLI retains the full build output and reports the actual failure category after WSLC exits.

Create a persistent data volume once:

```powershell
wslc volume create mockapi-data
```

Run the image with the initial resource limits:

```powershell
wslc run --detach `
  --name mockapi-dev `
  --cpus 0.5 `
  --memory 256M `
  --publish 8080:8080 `
  --volume=mockapi-data:/data `
  mockapi:dev
```

WSLC `2.9.3.0` requires the equals form for volume arguments. It accepts `--volume mockapi-data:/data` without an error but silently starts the container without the mount.

Inspect the running container and capture resource use:

```powershell
wslc inspect mockapi-dev
wslc stats --format table mockapi-dev
wslc logs mockapi-dev
```

The container reads `/data/mockapi.json` by default and starts with an empty configuration when that file is absent. Set `MockApi__AllowEmptyConfiguration=false` when a missing configuration must fail startup. Exercise HTTP endpoints from Windows, then recreate the container with the same volume to verify persistence. The developer CLI smoke-tests `/health/ready` and the administrative dashboard at `/`, expecting HTTP `200` from both:

Before starting a new container, the developer CLI uses a short-lived root maintenance container to set the named volume root to the numeric `app` identity (`1654:1654`). The application container itself always runs as the non-root `app` user. This initialization also repairs volumes created by older MockAPI images without deleting their contents. WSLC `2.9.3.0` can incorrectly return exit code `137` during automatic `--rm` teardown even when the maintenance command succeeded, so the CLI names the maintenance container and removes it explicitly after it exits.

The developer CLI records the immutable image ID on each created container. When `container-run` finds a running or stopped container whose recorded ID differs from the current tagged image, it recreates the container while retaining the named data volume. Containers created before image-ID tracking are recreated once to establish the label. A running container is reported as already running only when its recorded image ID matches the current image.

Removing dashboard assets makes only a very small difference to image size, so build `Dockerfile` for every deployment. Pass `MockApi__EnableDashboard=false` when the dashboard should not be deployed:

```powershell
wslc run --detach --name mockapi-no-dashboard `
  --env=MockApi__EnableDashboard=false `
  --publish 8080:8080 `
  --volume=mockapi-data:/data `
  mockapi:dev
```

The dashboard assets remain in the image but are not served. To disable every administrative surface, also pass `MockApi__EnableManagementApi=false`, `MockApi__EnableOpenApi=false`, and `MockApi__EnableSwaggerUi=false`.

On WSL kernels without swap-accounting support, WSLC reports `Memory limited without swap` when the container starts. This is a host capability warning: the configured 256 MiB memory limit is still applied, but WSLC cannot enforce a separate swap limit. The developer CLI explains this before launch and reports the application URL only after readiness and dashboard smoke tests pass.

```powershell
Invoke-WebRequest http://localhost:8080/ -SkipHttpErrorCheck
wslc stop --time 1 mockapi-dev
wslc remove mockapi-dev
```

The developer CLI uses the same one-second graceful shutdown window for `container-stop`. WSLC sends the container its normal termination signal and forces the stop only if the process has not exited after that window, avoiding the default five-second wait when graceful shutdown stalls.

Repeat the `wslc run` command with `mockapi-data:/data` and confirm that automatically saved configuration is restored.

## Measured Image Footprint

Container builds compile a self-contained Native AOT executable in the Alpine AOT SDK, then copy only the publication into the runtime-deps image. The final stage carries no SDK, native compiler, managed runtime, JIT, or debug symbols. Local managed development and tests still use CoreCLR.

### Native AOT validation

The strategy changed on 2026-09-25 to prioritize idle memory over image size. Native ARM64 image `mockapi:build-20260925T181011Z` (`4c398045265f`) was built with compiler, trimming, and AOT warnings treated as errors and exercised using WSLC `2.9.13.0` with `0.5` CPU and `256 MiB` memory limits.

| Measurement                                           | Observed value   |
| ----------------------------------------------------- | ---------------- |
| WSLC-reported image size                              | 39,630,203 bytes |
| Published application payload                         | 27,997,414 bytes |
| Idle memory, before administrative probes             | 16.23 MiB        |
| Memory after OpenAPI, Swagger UI, and mutation probes | 24.50 MiB        |
| Memory after recreation and 200 mock requests         | 19.65 MiB        |
| Host-observed container run command to readiness      | 1,589 ms         |

The initial and post-probe CPU snapshots were `0.62%` and `0.43%`; after recreation and mock traffic the snapshot was `0.41%`. These are point-in-time observations, not a sustained-load benchmark. Startup timing includes the container engine and host HTTP probe, not just application initialization.

Native-only publish contents, process UID/GID `1654`, dashboard, OpenAPI (including nullable query parameters), Swagger UI assets, runtime endpoint mutations, configuration imports and exports, rate-limit behavior, statistics, saves, persistence after container recreation, absence of the `Server` header, and graceful shutdown with exit code `0` were exercised. WSLC still does not expose read-only-root enforcement or platform selection; AMD64 execution, read-only enforcement, registry transfer sizes, SBOM scanning, and index publication remain CI validation requirements.

The historical image below predates the current application and is not an equivalent CoreCLR/AOT comparison. Do not attribute the entire size difference to AOT or reuse the historical memory savings as a current-build measurement.

### Historical CoreCLR baseline

The following baseline was measured on 2026-07-11 from native branded validation image `mockapi:branding-validation` (`cebec3ff3f54`) using WSLC `2.9.3.0`:

| Measurement                             |      Bytes | Decimal MB | Binary MiB |
| --------------------------------------- | ---------: | ---------: | ---------: |
| WSLC-reported image size                | 28,101,107 |   28.10 MB |  26.80 MiB |
| Uncompressed layer tar streams          | 28,422,144 |   28.42 MB |  27.11 MiB |
| Estimated gzip-compressed layer payload | 14,976,761 |   14.98 MB |  14.28 MiB |
| Uncompressed OCI archive with metadata  | 28,446,208 |   28.45 MB |  27.13 MiB |

`wslc image inspect mockapi:branding-validation` provides the WSLC-reported size. WSLC exports this local image with uncompressed OCI layer tar streams, so the compressed estimate was calculated by gzip-compressing each exported layer independently with .NET's optimal compression level and summing the results. The resulting payload is approximately 53% of the uncompressed layer tar size.

The reconstructed `/app` layer contains 19 runtime files, including the SVG favicon and its two compressed static variants, and no PDB, Node.js, Playwright, Vitest, test, documentation, package-manager, or development-manifest artifacts. The favicon adds 7,824 bytes to the native image; the complete 100%-covered branded image is only 22,559 bytes larger than the preceding `mockapi:dev` image measured on the same host.

Treat the compressed value as a reproducible local estimate, not an exact registry transfer size. Registry compression settings can produce a slightly different result. Release validation must record the exact compressed descriptor sizes for both `linux/amd64` and `linux/arm64` from the published multi-platform image manifest.

## Image Support Matrix

MockAPI is distributed as a Linux container. The release tag must be a multi-platform OCI image index containing both `linux/amd64` and `linux/arm64`; a compatible container engine selects the matching image automatically.

| Host operating system | Host CPU      | Required image platform | Support                                                          | Validation status                     |
| --------------------- | ------------- | ----------------------- | ---------------------------------------------------------------- | ------------------------------------- |
| Windows               | x64           | `linux/amd64`           | Supported through a Linux container engine such as WSL 2         | CI runtime validation required        |
| Windows               | ARM64         | `linux/arm64`           | Supported through a Linux container engine such as WSLC or WSL 2 | Validated locally with WSLC `2.9.3.0` |
| Linux                 | x64           | `linux/amd64`           | Supported natively                                               | CI runtime validation required        |
| Linux                 | ARM64         | `linux/arm64`           | Supported natively                                               | CI runtime validation required        |
| macOS                 | Intel x64     | `linux/amd64`           | Supported through a Linux container engine                       | CI runtime validation required        |
| macOS                 | Apple silicon | `linux/arm64`           | Supported through a Linux container engine                       | CI runtime validation required        |

The image is not a native Windows container and does not contain Windows or macOS binaries. Windows and macOS hosts run the matching Linux image in their container engine's Linux virtual machine. Publishing only one architecture can cause startup failures or emulation on a host with the other architecture, so release tags must not be published until both image variants have been built, run, and assembled into the shared index. Prefer a named volume for `/data` to avoid host-specific bind-mount sharing and permission behavior, especially on macOS.

## Multi-Architecture Release Boundary

Native AOT release builds require the target Linux OS and native toolchain. CI uses `ubuntu-24.04` for `linux-musl-x64` and `ubuntu-24.04-arm` for `linux-musl-arm64`, with compilation taking place inside the matching Alpine AOT SDK container. Local Windows/macOS development uses the Linux container engine rather than attempting cross-OS AOT publication.

The developer CLI `publish` action still cross-publishes trimmed CoreCLR artifacts for both runtime identifiers as a managed-code diagnostic. It explicitly sets `PublishAot=false`; these artifacts are not shipped. The `container-build` action is the Native AOT build path. Managed unit tests and coverage remain available without a native compiler installed on the host.

WSLC `2.9.3.0` has no `--platform` option on `build`, `pull`, or `run`, and no image-index command. Therefore:

- Local WSLC builds and runtime tests cover the native `linux/arm64` image.
- Matching native CI runners build and test both `linux/amd64` and `linux/arm64` images with no QEMU compilation.
- CI enables the OCI-capable Docker image store on disposable hosted runners, then archives each tested and scanned image. The publication job loads those same archives, verifies image configuration digests and platforms, and uploads them under run-specific candidate tags.
- CI assembles the multi-platform OCI image index from those uploaded digests, verifies exactly the two expected platforms and digests, and only then creates the release tag. No application compilation occurs in the publication job, and an existing release tag is rejected.
- CI performs read-only-root, SBOM, vulnerability, and architecture-parity checks that local WSLC cannot complete.

Both container workflows run `scripts/Assert-PublishContents.ps1 -NativeAot` and `scripts/Test-PublishedApplication.ps1` against the image, then recreate the container with its existing volume and run the persistence check. Enable OpenAPI and Swagger UI for the first smoke run so optional administrative surfaces cannot silently regress under AOT. Local smoke tests use no dashboard credentials.

Do not treat a successful managed cross-publish as runtime validation of either native image. Record any skipped local check and require its corresponding CI result before declaring a release ready. Registry publication still requires explicit approval and the public-release audit.
