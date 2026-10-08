# Changelog

All notable changes to MockAPI are documented here, starting with version 1.0.1.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and application versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.0] - 2026-10-08

### Changed

- Built-in examples now form a cohesive Contoso Theme Parks (`/ctp`) API with linked park, attraction,
  reservation, and wait-time fixtures, plus clearly labeled fault demos. Existing example IDs remain
  stable; previously loaded `/ex` routes migrate only after conflict review and explicit force update.
- Both developer CLI showcases exercise the themed wait-times endpoint and detect legacy routes
  even when their stable endpoint ID is already loaded.
- Documentation-site carousel controls are clearer and more compact across desktop and mobile layouts.
- Development dependencies were updated to their latest eligible compatible releases.

### Fixed

- Dark-mode table hover backgrounds preserve accessible contrast for colored response-status text.

## [1.2.0] - 2026-10-02

### Added

- Anonymous initial API-key setup when administrator credentials are not configured, while preserving authentication for security settings when credentials are configured.
- Interactive retry for failed developer CLI actions in both PowerShell and Bash.
- A dashboard link to the public MockAPI documentation site.

### Changed

- API-key protection changes save automatically when the dashboard checkbox changes and restore the saved state after cancellation or failure.
- Documentation-site analytics use the approved direct GA4 tag only on the production origin, omit query strings and referrers, disable advertising signals, and honor the browser opt-out flag.
- Settings use clearer security status styling and can be dismissed by clicking outside the dialog.
- Development dependencies were updated to their latest eligible compatible releases.

### Fixed

- Configuration and security-document saves retry transient file replacement failures before reporting an error.
- Dashboard synchronization continues after an endpoint refresh fails, allowing a later refresh to recover.

## [1.1.0] - 2026-10-01

### Added

- Automatic configuration saving for endpoint edits, bulk operations, imports, built-in merges, and API descriptions.
- Explicit automatic-save failure reporting that keeps changes active and offers persistence retry without replaying edits.

### Changed

- The dashboard saves configuration changes automatically and offers **Retry save** when persistence fails.
- Release automation validates dated changelog entries before tagging and publishes their notes as GitHub releases.
- Approved container publication attaches evidence to the existing changelog release without replacing its notes.
- Application-version tests now verify presence, SemVer format, and metadata-derived consistency rather than a specific release.
- Project and versioning instructions require version bumps to leave synthetic version fixtures unchanged and avoid test-update churn.

### Fixed

- Exported OpenAPI application-version metadata now comes from the assembly instead of a hard-coded version.
- Endpoint table column headers remain on one line with consistent padding in the dashboard's columns layout.

## [1.0.1] - 2026-10-01

### Added

- Automatic GitHub releases after quality validation, with release notes from this changelog.
- Build timestamps in application version information.

### Changed

- Refined dashboard and documentation styling.
- Separately approved container publication attaches validation evidence to the existing GitHub release.

### Security

- Instance API-key protection for mock routes, with administrative key rotation and fail-closed defaults.

[Unreleased]: https://github.com/simonkurtz-MSFT/MockAPI/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.2.0...v1.3.0
[1.2.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.1.0...v1.2.0
[1.1.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.0.0...v1.0.1
