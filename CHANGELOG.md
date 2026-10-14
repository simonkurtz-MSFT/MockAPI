# Changelog

All notable changes to MockAPI are documented here, starting with version 1.0.1.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and application versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.6.0] - 2026-10-14

### Added

- Export selected enabled endpoints by stable ID from the dashboard and management API in native
  configuration and portable request/test formats. An empty selection continues to export all
  endpoints.

### Changed

- Statistics consistently count every mock-dispatch attempt, including failures, and retain a
  selected response status when a write fails.
- Metadata-only configuration reads avoid parsing endpoint definitions.

## [1.5.2] - 2026-10-14

### Changed

- Dashboard endpoint tables use clearer path and status emphasis, group spacing, tooltips, and
  API-description editing guidance.
- CI uses pinned Ubuntu 26.04 runners, and the development pnpm version is updated to 12.10.0.

### Fixed

- Container publication validates exact-commit quality evidence from one qualifying run, performs
  registry and credential checks before native builds, and refuses to replace an existing release
  image tag.

## [1.5.1] - 2026-10-13

### Changed

- Application builds now report version 1.5.1 through the existing project and assembly metadata
  surfaces, without changing runtime behavior, public contracts, or configuration compatibility.

## [1.5.0] - 2026-10-13

### Added

- Mock responses and intentional connection aborts can now use a bounded, cancellation-aware delay
  of 1 through 300 seconds.

### Changed

- Public-site links open external destinations in a new window, and the site footer displays the
  application version derived from project metadata.
- Configuration files that use `delaySeconds` require MockAPI 1.5.0 or later; older binaries reject
  the new field, while existing configuration files remain compatible with 1.5.0.

### Fixed

- Container publication resolves architecture-candidate digests unambiguously after pushing them.

## [1.4.4] - 2026-10-13

### Fixed

- Container validation now retries all transient readiness request failures after container
  recreation.
- Manual container release recovery accepts successful quality validation of the immutable release
  tag and reports the exact recovery command when evidence is missing.

## [1.4.3] - 2026-10-13

### Changed

- Expanded the Contoso Theme Parks example configuration with additional endpoints and clearer
  descriptions.
- Refined dashboard layout and endpoint testing interactions without changing mock routing,
  management API behavior, or the configuration schema.

## [1.4.2] - 2026-10-12

### Changed

- Dashboard and documentation-site copy, formatting, and styling are refined, with a clearer
  documentation-site feature overview.
- Container publication now follows the quality workflow's validated version tag and GitHub release
  automatically, while retaining manual dispatch for recovery of an existing validated tag.

### Fixed

- Azure Container Apps deployments keep one replica running instead of scaling to zero.

## [1.4.1] - 2026-10-12

### Added

- A browser-saved workspace preference switches request-log times and minute bucket labels between
  UTC and the browser's local time zone.

### Changed

- Request log uses one Time column with UTC as the default instead of showing separate local and UTC
  columns. Stored timestamps and minute bucket identities remain in UTC.
- Local developer CLI runs default to `http://localhost:5080` when `ASPNETCORE_URLS` is unset, so
  the documented run commands no longer require a separate environment-variable step.
- Dashboard and documentation-site links, copy, layout, and screenshots reflect the current public
  project presentation.

## [1.4.0] - 2026-10-09

### Added

- A persistent dashboard header indicator distinguishes unprotected mock APIs, API-key protection,
  a missing server key, and unavailable protection status without opening Settings.
- Endpoint tests check current API-key protection and show actionable missing-key guidance with
  emphasized header names and separate actions. Explicit request headers retain precedence over
  the memory-only Settings key, and deliberate unauthorized tests remain available.
- Existing operations display their stable operation ID as read-only in the edit dialog.

### Changed

- Information previews close when the pointer leaves their information button, including when it
  moves onto the preview.
- Supported HTTP methods appear in separate bordered badges.
- Endpoint information buttons precede operation names, and compact status and action columns leave
  more room for names and paths.
- Path now appears immediately after the selection checkbox, before the information button and
  operation name. Paths omit their API group prefix for display only; configured paths, editing,
  filtering, exports, and request URLs remain unchanged.
- Endpoint paging is replaced by vertical scrolling with sticky headers, showing all matching
  operations. API groups remain alphabetical, operations default to alphabetical name order within
  each group, and header clicks sort operations without reordering or expanding API groups.
- The separate Last attempt column is replaced by a hover tooltip on each operation's attempt count.

### Fixed

- Endpoint table columns remain stable when API groups collapse.
- Sort indicators stay within their headers, and the Attempts heading and values are centered.

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

[Unreleased]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.5.2...HEAD
[1.5.2]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.5.1...1.5.2
[1.5.1]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.5.0...1.5.1
[1.5.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.4.4...1.5.0
[1.4.4]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.4.3...1.4.4
[1.4.3]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.4.2...1.4.3
[1.4.2]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.4.1...1.4.2
[1.4.1]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.4.0...v1.4.1
[1.4.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.2.0...v1.3.0
[1.2.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.1.0...v1.2.0
[1.1.0]: https://github.com/simonkurtz-MSFT/MockAPI/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/simonkurtz-MSFT/MockAPI/compare/1.0.0...v1.0.1
