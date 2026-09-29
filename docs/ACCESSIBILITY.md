# Accessibility Verification

MockAPI uses WCAG 2.2 Level AA, including applicable WCAG 2.0 and 2.1 requirements, as the design and automated-testing target for the administrative dashboard. The automated scope covers light, dark, high-contrast, reduced-motion, desktop, and mobile states.

## Current Status

Automated semantic, keyboard, responsive, and axe-core checks are part of the Playwright suite. These checks provide repeatable regression evidence but do not, by themselves, establish formal WCAG conformance.

## Automated Evidence

- Frontend unit coverage: `pnpm run test:frontend:coverage`
- Backend coverage: `./start.ps1 -Action coverage` enforces 100% line and branch coverage for the source-only production scope
- Chromium smoke checks: `pnpm run test:browser:smoke`
- Complete Chromium matrix: `pnpm run test:browser`
- CI policy: `pnpm run validate:ci-policy` verifies that every GitHub Actions workflow reference is pinned to an immutable commit SHA
- CI evidence: Playwright HTML and JUnit output, with trace, screenshot, and video diagnostics retained on failure for 14 days under the Quality engineering workflow

Automated accessibility scans fail on moderate, serious, or critical axe violations. A narrowly justified rule exception must be documented, issue-linked, and reviewed; blanket rule suppression is prohibited.

## Release Evidence

Retain the automated reports with the release evidence and record known limitations and remediation issue links in the release notes. Report accessibility problems through the repository issue tracker; workflow-created issues must be assigned to `simonkurtz-MSFT`.
