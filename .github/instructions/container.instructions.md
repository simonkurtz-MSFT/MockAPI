---
description: "Use when changing Dockerfiles, container configuration, publication settings, Compose files, image hardening, or container tests for MockAPI."
name: "MockAPI Container"
applyTo: "**/{Dockerfile,Dockerfile.*,*.Dockerfile,.dockerignore,compose*.yml,compose*.yaml}"
---

# Container Instructions

- Build with the approved `mcr.microsoft.com/dotnet/sdk:10.0-alpine3.24` image and run with `mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine3.24` after verifying current tags.
- Publish self-contained for both `linux-musl-x64` and `linux-musl-arm64`, compressed single-file, and fully trimmed.
- Produce one multi-platform OCI image index for `linux/amd64` and `linux/arm64`, with identical tags and application behavior across architectures.
- Use WSLC as the local container CLI and keep commands consistent with `docs/WSLC.md`. Do not require Docker Desktop or the Docker CLI for local development.
- Keep the root `start.ps1` container actions aligned with the Dockerfile and `docs/WSLC.md`; use those actions as the normal local workflow and retain direct WSLC commands for troubleshooting.
- Treat local WSLC builds on the ARM64 development host as native `linux/arm64` validation only. WSLC `2.9.3.0` does not expose target-platform selection or image-index management; build and test both architecture images and assemble the OCI index in CI.
- Keep build tools and SDK content out of the final image.
- Run as the built-in non-root `app` user and listen on HTTP port 8080.
- Keep the root filesystem read-only compatible; `/data` is the only writable application mount.
- Do not install a shell, package manager, or diagnostic utility in the final image.
- Terminate TLS outside the container and suppress the Kestrel `Server` response header.
- Use platform HTTP probes rather than adding a shell-based image health check.
- Start deployment examples with a `0.25` CPU and `128 MiB` memory request and a `0.5` CPU and `256 MiB` memory limit. Increase them only when measured startup or representative-load evidence requires it.
- Use WSLC locally for build, run, resource limits, volume persistence, logs, inspection, and statistics. Defer read-only-root enforcement, SBOM generation, vulnerability scanning, and multi-platform index verification to CI when the installed WSLC does not support them.
- Validate both built images rather than inferring behavior from the Dockerfile. Use the `release-validation` skill for architecture parity, constrained-resource operation, non-root, read-only, persistence, image-size, startup, SBOM, and vulnerability checks.
