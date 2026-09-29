---
name: public-release-audit
description: "Audit and scrub MockAPI before making the repository, source archive, package, container, SBOM, or release public. Use for public release, repository hygiene, secret scanning, history scanning, private-path cleanup, generated-artifact review, or publication readiness."
argument-hint: "Describe the repository, artifact, or release surface to audit"
---

# Public Release Audit

Use this skill before exposing repository content or publishing a release artifact. Treat the tracked tree, reachable Git history, generated archives, application publication, container image, and SBOM as separate disclosure surfaces.

## Procedure

1. Read `.github/copilot-instructions.md`, check `git status --short`, and preserve unrelated user changes.
2. Inventory tracked files with `git ls-files`. Review ignored and untracked files only to confirm exclusion boundaries; never add local state or generated output merely to inspect it.
3. Scan the current tracked tree with an approved secret scanner when available. Also search for private URLs, feeds, hostnames, email addresses, tenant/subscription/resource IDs, machine-specific paths and usernames, deployment names, approval history, realistic sample credentials, certificates, keys, local state, generated output, and transient plans.
4. Scan every reachable ref and commit with a history-capable secret scanner. A working-tree grep is not a history scan. If the required scanner is unavailable, perform the narrowest safe fallback checks and report the history scan as incomplete.
5. Classify every match as sensitive, private operational detail, intentional public metadata, obvious placeholder, or isolated test fixture. Record why an exception is safe; do not hide broad paths or patterns to make the scan pass.
6. Inspect `.gitignore`, `.dockerignore`, package and publish rules, source archives, container layers, and SBOM contents. Confirm that `.env`, `.azure`, IDE state, build output, test results, coverage, logs, local persistence, debug symbols, and development-only dependencies stay out of public artifacts.
7. Review documentation, examples, configuration, workflows, scripts, instruction files, skills, and agents for internal references and guidance that could reintroduce disclosure.
8. Remove unnecessary private or transient content and replace required examples with unmistakable placeholders. Keep intentional public project URLs, professional ownership metadata, and test-only values only when their purpose is clear.
9. If a real credential or token appears anywhere in history, do not reproduce it. Record only its type and location, rotate or revoke it first, then coordinate history rewriting and clone invalidation. Deleting the current file does not remediate the exposure.
10. Rerun the same scans after remediation, run `git diff --check` and `./start.ps1 -Action lint`, and inspect the final diff for unrelated changes.

## Required Checks

- [ ] No credential, token, private key, certificate, or realistic secret is tracked or present in reachable history.
- [ ] No unnecessary private endpoint, internal feed, personal email, cloud identifier, machine path, deployment name, or approval record remains.
- [ ] Public identifiers and test fixtures are explicitly classified and safe.
- [ ] Local state, transient plans, and generated outputs are ignored and absent from tracked and published content.
- [ ] Source archives, application publications, container layers, and SBOMs contain only intended runtime or distribution files.
- [ ] Scan tools, scope, commands, exceptions, unavailable checks, and residual risks are reported without disclosing secret values.

## Output

Lead with actionable findings ordered by severity. For each finding, provide the path and line, disclosure risk, classification, remediation, and validation. Conclude with the exact surfaces scanned, explicit exceptions, unavailable checks, and a release-readiness verdict. Never include a complete discovered secret in the report.
