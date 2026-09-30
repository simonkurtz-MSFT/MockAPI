# MockAPI Project Guidelines

## Source of Truth

- Read `docs/PLAN.md` before making architectural or behavioral changes.
- Treat the plan's route rules, configuration contract, security guardrails, and acceptance criteria as requirements until superseded by an approved design change.
- Keep this file and the nearest scoped instruction or skill current when project invariants, ownership boundaries, or validation commands change.

## Architecture

- Build one ASP.NET Core .NET 10 application and one deployable container.
- Keep reserved management and health routes separate from the catch-all mock dispatcher.
- Apply configuration changes by validating a complete candidate and atomically replacing an immutable registry snapshot. Never partially mutate the active configuration.
- Keep configuration models, registry, dispatcher, persistence, statistics, management API, and dashboard responsibilities distinct.
- Preserve stable endpoint IDs across edits and use them for statistics attribution.
- Keep statistics bounded and process-local; do not retain sensitive request or response content.
- Keep the approved Google Tag Manager container confined to the public documentation site. Do not
  add analytics to the runtime dashboard or mock endpoints; stub the container in site browser tests.

## Engineering Practices

- Write code for readers ranging from junior developers to experienced maintainers. Prefer straightforward control flow, descriptive names, and explicit intermediate steps over terse, clever, or densely composed syntax.
- Keep functions cohesive and small enough to understand without tracing unrelated state. Extract a well-named local or helper when it reveals intent, but do not fragment a simple operation across unnecessary abstractions.
- Make behavior and ownership visible in the code. Avoid hidden side effects, ambiguous Boolean parameters, unexplained constants, and conditionals whose meaning must be reconstructed by the reader.
- Explain why non-obvious behavior is necessary, especially for security, protocol, concurrency, lifecycle, compatibility, and accessibility constraints. Place concise comments near the relevant code, keep them current, and do not narrate statements that are already clear.
- Prefer readability over micro-optimization. Use a less obvious implementation only when measurements or a hard platform constraint justify it, and document the reason and invariant that future changes must preserve.
- Make tests readable specifications: use behavior-focused names, arrange setup and assertions clearly, and favor expected outcomes over implementation details.
- Prefer the .NET platform and `System.Text.Json` over new dependencies. Justify added packages by concrete functionality or risk reduction.
- Preserve trimming compatibility and use source-generated JSON metadata for application contracts.
- Compile container releases with Native AOT on matching AMD64 and ARM64 Linux runners, then assemble one multi-platform index from the exact tested images. Keep local managed development and diagnostic cross-publication separate from release-image validation.
- Reject invalid, conflicting, oversized, or unsafe endpoint definitions before activation.
- Keep management and health paths reserved and reject controlled or hop-by-hop response headers.
- When dashboard credentials are configured, protect every administrative surface. Health remains public; mock
  routes require an instance API key by default. Security Settings always require configured administrative
  credentials. Store only the administrator's salted password hash and the random API key's SHA-256 hash,
  separately from endpoint configuration, and require HTTPS outside loopback.
- Make focused changes and add tests at the lowest layer that can prove the behavior.
- Do not create GitHub Actions or Dependabot configuration until the plan phase that introduces them.

## Public Release Hygiene

- Before making the repository, an artifact, or a release public, use the `public-release-audit` skill to inspect both the current tracked tree and reachable Git history.
- Look for credentials and tokens; private URLs, feeds, hostnames, email addresses, tenant/subscription/resource identifiers; machine-specific paths and usernames; deployment names and approval history; generated output; local state; and transient planning files.
- Keep `.env.example` placeholder-only. Keep `.env`, `.azure`, IDE state, test results, coverage, publish output, and other generated or environment-specific content untracked and outside published artifacts.
- Distinguish intentional public project metadata and obvious test fixtures from sensitive values, but document that classification instead of silently suppressing a match.
- Never print a complete discovered secret in logs or reports. If a real secret was committed, rotate or revoke it before coordinating history cleanup; deleting it from the current tree is not sufficient.
- Do not claim a clean history scan when the required scanner was unavailable or only the working tree was inspected. Record scope, commands or tools, findings, exceptions, and residual gaps.

## Repository Automation

- Assign every issue created or reused by a GitHub Actions workflow to the professional GitHub account `simonkurtz-MSFT`.
- Keep the issue recipient in one clearly named workflow-level environment variable and use that variable for issue creation and `gh issue edit --add-assignee` calls.
- When automation deduplicates against an existing open issue, reapply `simonkurtz-MSFT` to that issue before reporting it as the active review item.
- Do not use the personal `simonua` account for MockAPI issue or pull-request assignment.
- Pin dependencies, development tools, and package managers to the latest compatible stable release that has completed the eight-day cooldown. Keep exact versions in reviewed manifests and lockfiles, preserve package-manager age enforcement, and verify publication age before selecting a release manually. Use `global.json` with a `10.0.100` baseline, `latestFeature` roll-forward, and prerelease SDKs disabled so local commands select the latest installed stable .NET 10 feature band; keep container base images immutable by digest.
- Run `pnpm run validate:dependency-security` in quality CI after restoring dependencies so known pnpm and direct or transitive NuGet vulnerabilities fail the build.
- Treat `pnpm-lock.yaml` as generated output owned by the pinned pnpm version. Regenerate it with pnpm and do not reformat it with Prettier. Keep it registry-neutral by preserving `lockfileIncludeTarballUrl: false` and the post-install and pre-commit normalization that removes redundant tarball URLs only when integrity hashes remain.
- Keep the mirrored `dependencies-update` and `pnpm-update` developer-CLI actions cooldown-aware. The pnpm updater must select the newest eligible stable release from registry publication metadata and keep `packageManager` and `engines.pnpm` synchronized.
- Keep `@playwright/test` excluded from package-only automatic updates. Update it in the same reviewed change as both digest-pinned Playwright CI image references, and validate the package and image publication ages together.
- Pin every external GitHub Action to a full 40-character immutable commit SHA and retain its release tag in a trailing comment for maintainability. Pin `docker://` workflow actions by `sha256` digest. Never use mutable tags or branches in workflow `uses:` references.
- Use the latest action release that satisfies the eight-day cooldown. JavaScript actions must declare Node 24 or newer; do not use runtime-forcing environment variables as a substitute for upgrading the action.
- Give every workflow job an explicit `timeout-minutes` from 1 through 60, sized to its expected workload rather than relying on GitHub's six-hour default.
- Give expensive push and pull-request workflows positive `paths` allowlists containing the workflow itself and every direct build, test, policy, or packaging input. Exclude documentation-only and repository-customization-only changes unless a job validates those files. Keep release, schedule, and manual triggers unconditional, and verify branch-protection behavior before path-filtering a required workflow.
- Run `pnpm run validate:workflow-pins` after changing any workflow and before committing automation changes.

## Versioning

- Use `.github/agents/versioning.agent.md` when selecting, applying, or validating an application version or release tag.
- Use `.github/skills/versioning/SKILL.md` for version-bump and release-tag tasks; follow `docs/RELEASING.md`.
- Treat `<Version>` in `src/MockAPI/MockAPI.csproj` as the single application-version source and use release tags in exact `v<Version>` form.
- Keep `package.json` private and unversioned. Validated version changes on `main` are tagged by the quality workflow; ordinary commits never move a tag.
- Keep public container publication and Pages deployment opt-in. Release publication requires manual approval, exact-commit quality evidence, and native image validation. Stage only the explicit static-site asset allowlist for Pages; never upload the repository root.
- Keep Codespaces port 5080 private. The development container is not the production image and must not mount a Docker socket or request cloud credentials.
- Keep the configuration `schemaVersion` independent from the application version; change it only for an approved configuration-contract revision.
- Never reuse or move a published version tag, and do not create a tag, release, or published artifact without explicit user approval.

## Validation

- Use the narrowest relevant test first, then run the broader project checks affected by the change.
- Keep Markdown lint-clean: every change must pass `pnpm run lint:markdown` (run through the `lint` action) with zero violations. Do not disable, suppress, or scope-narrow a rule to hide a real defect; only change `.markdownlint.jsonc` for an intentional, documented convention, and record the rationale inline in that file.
- Run local container smoke tests without dashboard credentials by leaving the optional dashboard username blank. Configure credentials only for authentication-specific tests or protected deployments.
- Use `start.ps1` (PowerShell 7) and `start.sh` (bash) as the canonical local entry points for setup, formatting, linting, backend/frontend coverage validation, managed-code validation, publication, and WSLC container workflows; keep their help and documentation current when commands change.
- Both `start.ps1` and `start.sh` are mandatory and must always exist together at the repository root. Never add, rename, or remove one without doing the same to the other in the same change.
- Keep `start.ps1` and `start.sh` completely aligned: identical actions, identical interactive-menu sections/entries/ordering/choice keys, identical option set, identical help text, and equivalent behavior and output. Mirror `-Action <name>`/`-ContainerEngine <engine>`/`-InstallMissing` (and every other parameter) as `--action <name>`/`--container-engine <engine>`/`--install-missing` (and the matching kebab-case long flag). Edit both scripts and their tests in the same change, and diff the two scripts' action lists, menu, options, and help before considering the change complete. When documentation shows a developer-CLI or PowerShell-cmdlet command, show the bash equivalent alongside it; pure `docker`/`docker compose` commands are identical across shells and are shown once.
- Keep Azure image actions distinct: interactive action `a4` uses `azure-deploy` to update the Container App and verify readiness, while `a5`/`azure-push` uses `azd publish` for an explicitly registry-only push.
- When custom-domain DNS is pending in menu mode, offer `4` to retry only domain setup and HTTPS verification. Any other key returns without clearing output or pausing again. Keep explicit command-line actions non-interactive and mirror continuation behavior in both shells.
- Keep the interactive home menu to Run locally, Verify, Azure, Containers, Setup, Help, and Quit. Group actions in submenus with Back, Help, and Quit; retain existing action shortcuts from every menu and keep command-line actions independent of navigation. Validate menu behavior and exact cross-shell output with `pwsh .\tests\DeveloperCli.Tests.ps1 -MenuOnly` (PowerShell) or `pwsh ./tests/DeveloperCli.Tests.ps1 -MenuOnly` (bash).
- Keep composed Azure pathways distinct and mirrored: `p1`/`pathway-azure-initial` runs tests, build, and `azure-up`; `p2`/`pathway-azure-update` runs tests, build, and `azure-deploy`.
- Keep Run locally as a submenu: `1`/`run` launches without the tutorial and `2`/`run-tutorial` launches with it. Both shells open the dashboard after readiness using `?tutorial=skip` or `?tutorial=show`, without resetting saved browser preferences. Keep ordinary dashboard URLs preference-driven and preserve `l1` as a legacy `run` shortcut.
- For cross-cutting endpoint contract changes, use the `mock-endpoint-change` skill.
- For trimming, container, or release-readiness work, use the `release-validation` skill.
- For public-release preparation or repository scrubbing, use the `public-release-audit` skill.
- Do not claim container properties such as non-root execution, read-only compatibility, or persistence until they have been exercised against the built image.
