---
name: release-validation
description: "Validate MockAPI publishing, trimming, minimal container hardening, persistence, health, image size, SBOM, and vulnerability posture. Use for Dockerfile, release, runtime image, or deployment-readiness changes."
argument-hint: "Describe the publication or container change to validate"
---

# Release Validation

Use this skill after publication, container, or release-related changes. Derive exact commands from the checked-in project and documentation rather than assuming paths or image names.

## Procedure

1. Read `docs/PLAN.md` and the container-specific instructions.
2. Use the `public-release-audit` skill before publishing. Scan the tracked tree and reachable history, inspect the publish and container contents, and report unavailable scanners or unverified scope as release gaps.
3. Verify current .NET 10 Alpine SDK and runtime-deps tags against official Microsoft documentation before changing pinned images.
4. Restore, build, and test the solution in Release configuration.
5. Publish Native AOT `linux-musl-x64` and `linux-musl-arm64` in the pinned Alpine AOT SDK on matching native runners. Pass `PublishAot=true` to restore and publish; use `scripts/Assert-PublishContents.ps1 -NativeAot` to reject debug symbols and managed runtime files. The CLI `publish` action remains a diagnostic CoreCLR check, not release publication.
6. Treat new compiler, analyzer, trim, and AOT warnings as failures unless a narrow suppression is documented and tested.
7. Use the commands and capability boundaries in `docs/CONTAINERS.md` for local container validation. Leave the optional dashboard username blank so local smoke tests run without dashboard credentials; configure credentials only for authentication-specific tests. On the ARM64 development host, use WSLC to build and run the native `linux/arm64` image with the required limits, volume, ports, inspection, logs, and statistics.
8. In CI, build `linux/amd64` and `linux/arm64` images natively, transfer the exact validated image archives, and assemble their multi-platform OCI index without rebuilding or QEMU compilation. Verify loaded and uploaded configuration digests, index membership, and compressed layer sizes. Do not infer AMD64 image validity from a managed cross-publish.
9. Run each architecture on a native runner or documented emulation and verify:
   - The process uses a non-root identity.
   - Port 8080 serves readiness, dashboard, management, and mock routes as configured.
   - The root filesystem can be read-only with only `/data` writable.
   - Saved configuration survives container recreation with the same data volume.
   - No `Server` header is emitted.
   - Startup and graceful shutdown succeed.
   - Application behavior is equivalent across architectures.
   - `scripts/Test-PublishedApplication.ps1` passes with OpenAPI and Swagger UI enabled, and its `-VerifyPersistence` check passes after container recreation.
10. Run the container with a `0.5` CPU and `256 MiB` memory limit. Exercise startup, health checks, dashboard use, persistence, and representative mock traffic; record idle and loaded CPU and memory observations with `wslc stats` locally or equivalent CI tooling.
11. Verify deployment examples request `0.25` CPU and `128 MiB` memory and cap usage at `0.5` CPU and `256 MiB`, unless measured evidence in the repository justifies different values.
12. In CI, verify read-only-root operation, generate or verify the SBOM, confirm it contains no excluded or local-state content, and scan both architecture images for vulnerabilities because WSLC `2.9.3.0` does not expose those checks.
13. Compare per-architecture image size and cold startup with the last recorded baseline; explain meaningful regressions.
14. Report commands, measured results, skipped checks, accepted vulnerability exceptions, and residual risks.

Follow `docs/RELEASING.md` for the approval boundary. Validate `v<Version>` against its resolved
commit and successful quality run before native builds. Automatic version tagging does not
publish images; publication requires the opt-in variable and an explicitly approved manual run.
Keep release SBOMs, scan output, resource observations, and image/index metadata as durable
GitHub release assets after the exact tested index is published.

Do not claim release readiness when a required check was inferred, unavailable, or skipped. Keep TLS termination outside the container and do not install troubleshooting tools in the final image.
