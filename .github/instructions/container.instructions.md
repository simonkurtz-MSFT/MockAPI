---
description: "Use when changing Dockerfiles, container configuration, publication settings, Compose files, image hardening, or container tests for MockAPI."
name: "MockAPI Container"
applyTo: "**/{Dockerfile,Dockerfile.*,*.Dockerfile,.dockerignore,compose*.yml,compose*.yaml}"
---

# Container Instructions

- Build with the approved `mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24-aot@sha256:574e01812ecd2f33a79cb61b0398320abbd0589125b1e0a6fea00806ba630cb4` image and run with `mcr.microsoft.com/dotnet/runtime-deps:10.0.13-alpine3.24@sha256:1a5dbee0a1d2bb1d038e212e8ccdbc12783b5cfbed866fe75d126ee80178a52a`. These images were accepted before the normal dependency cooldown as an explicit security exception because they upgrade Alpine zlib to `1.3.2-r1`, which fixes CVE-2026-85091.
- Publish Native AOT for both `linux-musl-x64` and `linux-musl-arm64` on matching native runners. Pass `PublishAot=true` to both restore and publish; exclude native debug symbols and managed runtime artifacts.
- Produce one multi-platform OCI image index for `linux/amd64` and `linux/arm64`, with identical tags and application behavior across architectures.
- Transfer and publish the exact native images that passed validation; verify configuration digests and index membership rather than rebuilding in the index or publication job. Use the OCI-capable image store only on disposable CI runners.
- Keep the developer CLI `publish` action as a diagnostic CoreCLR cross-publication check. `container-build` is the native AOT release-image build path.
- Default to WSLC for local container actions and keep its commands consistent with `docs/CONTAINERS.md`. Also support Docker as an explicit developer CLI backend, without requiring Docker Desktop or the Docker CLI for local development.
- Keep the root `start.ps1` and `start.sh` container actions aligned with each other, `Dockerfile`, and `docs/CONTAINERS.md`; use those actions as the normal local workflow and retain direct WSLC commands for troubleshooting.
- Pass the first enabled host HTTPS NuGet source into container builds as `NUGET_SOURCE`, retain nuget.org as the portable Dockerfile fallback, and never copy host NuGet configuration or credentials into the build context or image.
- Preserve actionable container-build diagnostics: distinguish non-fatal builder metadata warnings from fatal registry connectivity, missing-manifest, Dockerfile, and publish failures; compare Windows and WSLC registry reachability when WSLC reports a network failure, and recommend refreshing the independent WSLC session before broader host-network repair.
- Treat local WSLC builds on the ARM64 development host as native `linux/arm64` validation only. WSLC `2.9.3.0` does not expose target-platform selection or image-index management; build and test both architecture images and assemble the OCI index in CI.
- Keep build tools and SDK content out of the final image.
- Keep Node.js, package-manager files, browser binaries, tests, coverage output, documentation, and debug symbols out of the Docker build context and final image; validate native publish contents with `scripts/Assert-PublishContents.ps1 -NativeAot`.
- Run as the built-in non-root `app` user and listen on HTTP port 8080.
- Keep the root filesystem read-only compatible; `/data` is the only writable application mount.
- Do not install a shell, package manager, or diagnostic utility in the final image.
- Terminate TLS outside the container and suppress the Kestrel `Server` response header.
- Use platform HTTP probes rather than adding a shell-based image health check.
- Start deployment examples with a `0.25` CPU and `128 MiB` memory request and a `0.5` CPU and `256 MiB` memory limit. Increase them only when measured startup or representative-load evidence requires it.
- Use WSLC locally for build, run, resource limits, volume persistence, logs, inspection, and statistics. Defer read-only-root enforcement, SBOM generation, vulnerability scanning, and multi-platform index verification to CI when the installed WSLC does not support them.
- Verify SBOM contents do not include excluded source, local state, credentials, or development-only artifacts. Treat unresolved high or critical image vulnerabilities as release blockers unless a time-bounded, issue-linked exception documents exposure and mitigation.
- Validate the built image rather than inferring behavior from `Dockerfile`. Use the `release-validation` skill for architecture parity, constrained-resource operation, non-root, read-only, persistence, image-size, startup, SBOM, and vulnerability checks.
- Use one `Dockerfile` for all deployments. Keep dashboard assets in the image because removing them yields negligible size savings; disable dashboard deployment with `MockApi__EnableDashboard=false`.
- Publish release images only after native architecture and OCI index validation. Push the multi-platform image to `simonkurtzmsft/mockapi:<Version>` and require the tag to exactly match the application version without a `v` prefix. Build both architectures from the resolved tag commit, with successful quality evidence for that exact commit.
- Follow `docs/RELEASING.md`: a newly validated version tag automatically calls the reusable container workflow after the GitHub release is created. Keep Docker Hub credentials in repository secrets. Retain manual dispatch only for recovery of an existing validated tag.
