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

## Engineering Practices

- Prefer the .NET platform and `System.Text.Json` over new dependencies. Justify added packages by concrete functionality or risk reduction.
- Preserve trimming compatibility and use source-generated JSON metadata for application contracts.
- Reject invalid, conflicting, oversized, or unsafe endpoint definitions before activation.
- Keep management and health paths reserved and reject controlled or hop-by-hop response headers.
- Make focused changes and add tests at the lowest layer that can prove the behavior.
- Do not create GitHub Actions or Dependabot configuration until the plan phase that introduces them.

## Repository Automation

- Assign every issue created or reused by a GitHub Actions workflow to the professional GitHub account `simonkurtz-MSFT`.
- Keep the issue recipient in one clearly named workflow-level environment variable and use that variable for issue creation and `gh issue edit --add-assignee` calls.
- When automation deduplicates against an existing open issue, reapply `simonkurtz-MSFT` to that issue before reporting it as the active review item.
- Do not use the personal `simonua` account for MockAPI issue or pull-request assignment.

## Validation

- Use the narrowest relevant test first, then run the broader project checks affected by the change.
- Use `start.ps1` as the canonical local entry point for setup, managed-code validation, publication, and WSLC container workflows; keep its help and documentation current when commands change.
- For cross-cutting endpoint contract changes, use the `mock-endpoint-change` skill.
- For trimming, container, or release-readiness work, use the `release-validation` skill.
- Do not claim container properties such as non-root execution, read-only compatibility, or persistence until they have been exercised against the built image.
