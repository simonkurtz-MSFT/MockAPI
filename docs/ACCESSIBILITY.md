# Accessibility Statement and Verification

MockAPI targets WCAG 2.2 Level AA, including applicable WCAG 2.0 and 2.1 requirements. This target applies to the administrative dashboard in light, dark, high-contrast, reduced-motion, desktop, and mobile states.

## Current Status

Automated semantic, keyboard, responsive, and axe-core checks are part of the Playwright suite. Automated checks cannot establish complete conformance. Do not describe MockAPI as WCAG-conformant until the manual checklist below is completed for a release candidate and every applicable A/AA failure is resolved or documented with an owned remediation issue.

## Automated Evidence

- Frontend unit coverage: `pnpm run test:frontend:coverage`
- Backend coverage: `./start.ps1 -Action coverage` enforces 100% line and branch coverage for the source-only production scope
- Chromium smoke checks: `pnpm run test:browser:smoke`
- Complete Chromium matrix: `pnpm run test:browser`
- CI evidence: Playwright HTML, JUnit, trace, screenshot, and video artifacts under the Quality engineering workflow

Automated accessibility scans fail on moderate, serious, or critical axe violations. A narrowly justified rule exception must be documented, issue-linked, and reviewed; blanket rule suppression is prohibited.

## Manual Release Checklist

- [ ] Keyboard-only operation reaches every control in a logical order without a trap.
- [ ] Focus is visible and restored after dialogs, confirmations, filtering, and the endpoint test blade.
- [ ] NVDA with Chromium on Windows announces landmarks, tables, forms, errors, status messages, and dialogs accurately.
- [ ] JAWS with Chromium on Windows completes endpoint CRUD, built-in merge, save, filtering, and test-blade workflows.
- [ ] TalkBack with Chrome on Android completes representative mobile workflows.
- [ ] Text resized to 200% remains readable and operable.
- [ ] Browser zoom at 400% reflows without two-dimensional page scrolling or clipped controls.
- [ ] WCAG text-spacing overrides do not hide, overlap, or truncate content.
- [ ] Windows forced-colors mode preserves visible boundaries, focus, state, and meaning.
- [ ] Light and dark themes meet text and non-text contrast requirements in normal, hover, focus, selected, disabled, success, warning, and error states.
- [ ] Reduced-motion preferences remove nonessential motion without losing status information.
- [ ] Touch targets and orientation changes remain usable on representative mobile devices.
- [ ] Errors identify the affected field, explain the problem, and provide correction guidance.

## Release Evidence

Record the release version, date, tester, browser/assistive-technology versions, completed checklist, known limitations, and remediation issue links in the release notes. Report accessibility problems through the repository issue tracker; workflow-created issues must be assigned to `simonkurtz-MSFT`.
