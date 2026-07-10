---
name: release-validation
description: "Validate MockAPI publishing, trimming, minimal container hardening, persistence, health, image size, SBOM, and vulnerability posture. Use for Dockerfile, release, runtime image, or deployment-readiness changes."
argument-hint: "Describe the publication or container change to validate"
---

# Release Validation

Use this skill after publication, container, or release-related changes. Derive exact commands from the checked-in project and documentation rather than assuming paths or image names.

## Procedure

1. Read `docs/PLAN.md` and the container-specific instructions.
2. Verify current .NET 10 Alpine SDK and runtime-deps tags against official Microsoft documentation before changing pinned images.
3. Restore, build, and test the solution in Release configuration.
4. Publish `linux-musl-x64` and `linux-musl-arm64` with the repository's compressed single-file, self-contained, and full-trimming settings.
5. Treat new compiler, analyzer, and trim warnings as failures unless a narrow suppression is documented and tested.
6. Use the commands and capability boundaries in `docs/WSLC.md` for local container validation. On the ARM64 development host, use WSLC to build and run the native `linux/arm64` image with the required limits, volume, ports, inspection, logs, and statistics.
7. In CI, build `linux/amd64` and `linux/arm64` images, publish or inspect their multi-platform image index, and record each image digest and compressed size. Do not infer AMD64 image validity from a .NET cross-publish.
8. Run each architecture on a native runner or documented emulation and verify:
   - The process uses a non-root identity.
   - Port 8080 serves readiness, dashboard, management, and mock routes as configured.
   - The root filesystem can be read-only with only `/data` writable.
   - Saved configuration survives container recreation with the same data volume.
   - No `Server` header is emitted.
   - Startup and graceful shutdown succeed.
   - Application behavior is equivalent across architectures.
9. Run the container with a `0.5` CPU and `256 MiB` memory limit. Exercise startup, health checks, dashboard use, persistence, and representative mock traffic; record idle and loaded CPU and memory observations with `wslc stats` locally or equivalent CI tooling.
10. Verify deployment examples request `0.25` CPU and `128 MiB` memory and cap usage at `0.5` CPU and `256 MiB`, unless measured evidence in the repository justifies different values.
11. In CI, verify read-only-root operation, generate or verify the SBOM, and scan both architecture images for vulnerabilities because WSLC `2.9.3.0` does not expose those checks.
12. Compare per-architecture image size and cold startup with the last recorded baseline; explain meaningful regressions.
13. Report commands, measured results, skipped checks, and residual risks.

Do not claim release readiness when a required check was inferred, unavailable, or skipped. Keep TLS termination outside the container and do not install troubleshooting tools in the final image.
