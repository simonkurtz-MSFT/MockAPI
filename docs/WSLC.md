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

The equivalent direct WSLC build command is:

```powershell
wslc build --pull --tag mockapi:dev .
```

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

The container reads `/data/mockapi.json` by default and starts with an empty configuration when that file is absent. Set `MockApi__AllowEmptyConfiguration=false` when a missing configuration must fail startup. Exercise HTTP endpoints from Windows, then recreate the container with the same volume to verify persistence. Until the planned health routes are implemented, the developer CLI smoke-tests `/` and expects the root service response `MockAPI is running.` with HTTP `200`:

```powershell
Invoke-WebRequest http://localhost:8080/ -SkipHttpErrorCheck
wslc stop mockapi-dev
wslc remove mockapi-dev
```

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
