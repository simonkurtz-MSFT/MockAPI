---
name: versioning
description: Select, apply, or validate a MockAPI semantic application version, automatic GitHub version tag, or approved container release. Use for version bumps, prerelease progression, release tags, and version consistency.
---

# MockAPI Versioning

1. Read `docs/PLAN.md`, `docs/RELEASING.md`, and `.github/agents/versioning.agent.md`.
2. Check the working tree, current project version, existing tags, and changes since the last relevant tag.
   Inspect compatibility and public behavior, not only commit messages.
3. Keep `<Version>` in `src/MockAPI/MockAPI.csproj` as the only application-version source.
   `package.json` is private development tooling: do not add an application version.
   Configuration `schemaVersion` is independent.
4. Recommend one exact SemVer version with a compatibility rationale. During the 1.0 prerelease
   sequence, advance the prerelease identifier unless the user approves a different stability stage.
   Do not promote to `1.0.0` merely because release infrastructure is ready.
5. Apply a bump only when requested. Search for intentional version assertions and update them;
   do not update examples to an image tag that has not been published.
6. Run `node scripts/release-version.cjs check` and the focused release-version tests, then the
   canonical lint action in both shell documentation:
   `./start.ps1 -Action lint` (PowerShell) / `./start.sh --action lint` (Bash).
7. A validated version change pushed to `main` is tagged by the final quality job. No-op changes
   do not move a tag. Existing historical unprefixed tags are left intact; new tags use `v<Version>`.
   GitHub token-created tags do not trigger another workflow: publication is explicitly dispatched.
   After a force-push, tagging explicitly fetches a missing nonzero pre-push commit before
   comparing versions. Fetch failures stop tagging; never treat missing history as a version bump.
8. Before publication, use the public-release-audit and release-validation skills. Require the
   successful quality run for the exact commit, native image evidence, and human approval.
   Never create a commit, tag, release, or publish an image during version selection or validation.

Report the current and proposed versions, compatibility rationale, tests, and remaining release
blockers. Identify the next operation requiring approval; do not claim release readiness from a
version check alone.
