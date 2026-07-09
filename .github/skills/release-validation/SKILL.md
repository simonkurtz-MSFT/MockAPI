---
name: release-validation
description: "Validate MockAPI publishing, trimming, chiseled container hardening, persistence, health, image size, SBOM, and vulnerability posture. Use for Dockerfile, release, runtime image, or deployment-readiness changes."
argument-hint: "Describe the publication or container change to validate"
---

# Release Validation

Use this skill after publication, container, or release-related changes. Derive exact commands from the checked-in project and documentation rather than assuming paths or image names.

## Procedure

1. Read `docs/PLAN.md` and the container-specific instructions.
2. Verify current .NET 10 Noble SDK and runtime-deps chiseled tags against official Microsoft documentation before changing pinned images.
3. Restore, build, and test the solution in Release configuration.
4. Publish the intended Linux runtime identifier with the repository's single-file, self-contained, and full-trimming settings.
5. Treat new compiler, analyzer, and trim warnings as failures unless a narrow suppression is documented and tested.
6. Build the final image and record its digest and compressed size.
7. Run the built image and verify:
   - The process uses a non-root identity.
   - Port 8080 serves readiness, dashboard, management, and mock routes as configured.
   - The root filesystem can be read-only with only `/data` writable.
   - Saved configuration survives container recreation with the same data volume.
   - No `Server` header is emitted.
   - Startup and graceful shutdown succeed.
8. Generate or verify the SBOM and scan the final image for vulnerabilities.
9. Compare image size and cold startup with the last recorded baseline; explain meaningful regressions.
10. Report commands, measured results, skipped checks, and residual risks.

Do not claim release readiness when a required check was inferred, unavailable, or skipped. Keep TLS termination outside the container and do not add troubleshooting tools to the final chiseled image.
