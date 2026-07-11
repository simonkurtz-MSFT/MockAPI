# WSLC Development Workflow

MockAPI uses the Windows Subsystem for Linux Container CLI (`wslc`) for local container builds and tests. The commands in this document were verified with WSLC `2.9.3.0` on a Windows ARM64 host.

Run `wslc version` and review command help before relying on these constraints with a newer release.

## Supported Local Work

| Capability | WSLC 2.9.3.0 | MockAPI use |
| --- | --- | --- |
| Build from a Dockerfile | Yes | Build the native ARM64 development image |
| Run and manage containers | Yes | Exercise health, management, dashboard, and mock routes |
| Limit CPU and memory | Yes | Test with the `0.5` CPU and `256 MiB` release limits |
| Named and bind-mounted volumes | Yes | Verify `/data` persistence across container recreation |
| Networks and published ports | Yes | Expose port `8080` for host-side HTTP checks |
| Image inspection, tagging, push, and pull | Yes | Inspect metadata and publish architecture-specific images |
| Resource statistics | Yes | Record CPU, memory, I/O, and process observations |
| Select a build, pull, or run platform | No | Cannot request `linux/amd64` on this ARM64 host |
| Create or inspect a multi-platform image index | No | Assemble and verify the release index in CI |
| Enforce a read-only root filesystem at run time | No exposed option | Verify on a CI runner with suitable container tooling |
| Generate an SBOM or scan vulnerabilities | No built-in command | Perform both release checks in CI |

WSLC uses Dockerfiles as its image build format; using `wslc` does not require Docker Desktop or the Docker CLI.

## Local ARM64 Workflow

Use the developer CLI from the repository root for the normal workflow:

```powershell
.\start.ps1 -Action container-build
.\start.ps1 -Action container-run
.\start.ps1 -Action container-test
```

The developer CLI gives each image a permanent UTC timestamp tag, then moves the `mockapi:dev` alias to the new image. The equivalent direct WSLC commands are:

```powershell
$buildImage = 'mockapi:build-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
wslc build --pull --tag $buildImage .
wslc image tag $buildImage mockapi:dev
```

Because every build retains its timestamp tag, moving `mockapi:dev` does not leave the previous image untagged. On the first build after adopting this convention, the developer CLI preserves an existing alias-only image using its creation timestamp and short image ID before moving the alias.

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
  --volume mockapi-data:/data `
  mockapi:dev
```

Inspect the running container and capture resource use:

```powershell
wslc inspect mockapi-dev
wslc stats --format table mockapi-dev
wslc logs mockapi-dev
```

The container reads `/data/mockapi.json` by default and starts with an empty configuration when that file is absent. Set `MockApi__AllowEmptyConfiguration=false` when a missing configuration must fail startup. Exercise HTTP endpoints from Windows, then recreate the container with the same volume to verify persistence. The developer CLI smoke-tests `/health/ready` and the administrative dashboard at `/`, expecting HTTP `200` from both:

Before starting a new container, the developer CLI uses a short-lived root maintenance container to set the named volume root to the numeric `app` identity (`1654:1654`). The application container itself always runs as the non-root `app` user. This initialization also repairs volumes created by older MockAPI images without deleting their contents. WSLC `2.9.3.0` can incorrectly return exit code `137` during automatic `--rm` teardown even when the maintenance command succeeded, so the CLI names the maintenance container and removes it explicitly after it exits.

The developer CLI records the immutable image ID on each created container. When `container-run` finds a running or stopped container whose recorded ID differs from the current tagged image, it recreates the container while retaining the named data volume. Containers created before image-ID tracking are recreated once to establish the label. A running container is reported as already running only when its recorded image ID matches the current image.

On WSL kernels without swap-accounting support, WSLC reports `Memory limited without swap` when the container starts. This is a host capability warning: the configured 256 MiB memory limit is still applied, but WSLC cannot enforce a separate swap limit. The developer CLI explains this before launch and reports the application URL only after readiness and dashboard smoke tests pass.

```powershell
Invoke-WebRequest http://localhost:8080/ -SkipHttpErrorCheck
wslc stop --time 1 mockapi-dev
wslc remove mockapi-dev
```

The developer CLI uses the same one-second graceful shutdown window for `container-stop`. WSLC sends the container its normal termination signal and forces the stop only if the process has not exited after that window, avoiding the default five-second wait when graceful shutdown stalls.

Repeat the `wslc run` command with `mockapi-data:/data` and confirm that explicitly saved configuration is restored.

## Measured Image Footprint

The native `linux/arm64` development image is deliberately small because the application is self-contained, published as a compressed single file, fully trimmed, and based on the minimal .NET runtime-deps image. The final stage carries neither the build SDK nor a shared .NET framework runtime.

The following baseline was measured on 2026-07-10 from image `mockapi:dev` (`536b01cc2e7b`) using WSLC `2.9.3.0`:

| Measurement | Bytes | Decimal MB | Binary MiB |
| --- | ---: | ---: | ---: |
| WSLC-reported image size | 26,844,770 | 26.84 MB | 25.60 MiB |
| Uncompressed layer tar streams | 27,152,896 | 27.15 MB | 25.90 MiB |
| Estimated gzip-compressed layer payload | 13,853,123 | 13.85 MB | 13.21 MiB |
| Uncompressed OCI archive with metadata | 27,174,912 | 27.17 MB | 25.92 MiB |

`wslc image inspect mockapi:dev` provides the WSLC-reported size. WSLC exports this local image with uncompressed OCI layer tar streams, so the compressed estimate was calculated by gzip-compressing each exported layer independently with .NET's optimal compression level and summing the results. The resulting payload is approximately 51% of the uncompressed layer tar size.

Treat the compressed value as a reproducible local estimate, not an exact registry transfer size. Registry compression settings can produce a slightly different result. Release validation must record the exact compressed descriptor sizes for both `linux/amd64` and `linux/arm64` from the published multi-platform image manifest.

## Image Support Matrix

MockAPI is distributed as a Linux container. The release tag must be a multi-platform OCI image index containing both `linux/amd64` and `linux/arm64`; a compatible container engine selects the matching image automatically.

| Host operating system | Host CPU | Required image platform | Support | Validation status |
| --- | --- | --- | --- | --- |
| Windows | x64 | `linux/amd64` | Supported through a Linux container engine such as WSL 2 | CI runtime validation required |
| Windows | ARM64 | `linux/arm64` | Supported through a Linux container engine such as WSLC or WSL 2 | Validated locally with WSLC `2.9.3.0` |
| Linux | x64 | `linux/amd64` | Supported natively | CI runtime validation required |
| Linux | ARM64 | `linux/arm64` | Supported natively | CI runtime validation required |
| macOS | Intel x64 | `linux/amd64` | Supported through a Linux container engine | CI runtime validation required |
| macOS | Apple silicon | `linux/arm64` | Supported through a Linux container engine | CI runtime validation required |

The image is not a native Windows container and does not contain Windows or macOS binaries. Windows and macOS hosts run the matching Linux image in their container engine's Linux virtual machine. Publishing only one architecture can cause startup failures or emulation on a host with the other architecture, so release tags must not be published until both image variants have been built, run, and assembled into the shared index. Prefer a named volume for `/data` to avoid host-specific bind-mount sharing and permission behavior, especially on macOS.

## Multi-Architecture Release Boundary

The .NET SDK on this ARM64 laptop can cross-publish Alpine-compatible application artifacts for both target runtime identifiers:

```powershell
dotnet publish src/MockAPI/MockAPI.csproj --configuration Release --runtime linux-musl-arm64
dotnet publish src/MockAPI/MockAPI.csproj --configuration Release --runtime linux-musl-x64
```

WSLC `2.9.3.0` has no `--platform` option on `build`, `pull`, or `run`, and no image-index command. Therefore:

- Local WSLC builds and runtime tests cover the native `linux/arm64` image.
- Native or otherwise qualified CI runners build and test both `linux/amd64` and `linux/arm64` images.
- CI assembles and verifies the multi-platform OCI image index.
- CI performs read-only-root, SBOM, vulnerability, and architecture-parity checks that local WSLC cannot complete.

Do not treat a successful cross-publish as runtime validation of the `linux/amd64` image. Record any skipped local check and require its corresponding CI result before declaring a release ready.
