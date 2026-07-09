---
description: "Use when changing Dockerfiles, container configuration, publication settings, Compose files, image hardening, or container tests for MockAPI."
name: "MockAPI Container"
applyTo: "**/{Dockerfile,Dockerfile.*,*.Dockerfile,.dockerignore,compose*.yml,compose*.yaml}"
---

# Container Instructions

- Build with the approved `mcr.microsoft.com/dotnet/sdk:10.0-noble` image and run with `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled` after verifying current tags.
- Publish self-contained for both `linux-x64` and `linux-arm64`, single-file, and fully trimmed.
- Produce one multi-platform OCI image index for `linux/amd64` and `linux/arm64`, with identical tags and application behavior across architectures.
- Keep build tools and SDK content out of the final image.
- Run as the built-in non-root `app` user and listen on HTTP port 8080.
- Keep the root filesystem read-only compatible; `/data` is the only writable application mount.
- Do not add a shell, package manager, or diagnostic utility to the final chiseled image.
- Terminate TLS outside the container and suppress the Kestrel `Server` response header.
- Use platform HTTP probes when the chiseled image has no suitable built-in health-check command.
- Start deployment examples with a `0.25` CPU and `128 MiB` memory request and a `0.5` CPU and `256 MiB` memory limit. Increase them only when measured startup or representative-load evidence requires it.
- Validate both built images rather than inferring behavior from the Dockerfile. Use the `release-validation` skill for architecture parity, constrained-resource operation, non-root, read-only, persistence, image-size, startup, SBOM, and vulnerability checks.
