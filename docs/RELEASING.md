# Versioning and Releases

MockAPI's next stable application version is 1.4.0. This version decision does not publish a release
or replace the quality, public-release audit, native image validation, and publication approval
gates below.

## 1.4.0 release summary

The planned release date is **2026-10-09**, with the immutable tag **`v1.4.0`**.
The [1.4.0 changelog entry](../CHANGELOG.md#140---2026-10-09) supplies the GitHub release notes.

- A persistent header indicator makes mock API protection visible without opening Settings.
- Endpoint tests explain how to supply a required key or explicitly disable request protection,
  while retaining deliberate unauthorized testing and response inspection.
- Existing operation IDs are visible as read-only in the edit dialog.
- Compact status and action columns give names and paths more room. Stable column widths, contained
  sort indicators, centered Attempts headings and values, and separate HTTP method badges improve
  dashboard readability. Last-attempt times are available by hovering over attempt counts instead
  of occupying a separate column.
- Information previews dismiss when the pointer leaves their button.
- Path appears after selection and before the information button and operation name, without the
  redundant API prefix. Full configured paths and request URLs remain unchanged.
- Vertical scrolling with sticky headers replaces paging and shows every matching operation.
  API groups stay alphabetical; operations default to alphabetical name order, and header clicks
  sort operations within expanded groups without changing group order or collapse state.

### Compatibility and upgrade behavior

The dashboard protection indicator, key guidance, and operation ID display add functionality
without changing the configuration schema (`schemaVersion: "1.0"`), so 1.4.0 is a minor release.
Existing endpoint IDs, configuration documents, mock routing, and API-key enforcement remain
unchanged. These dashboard changes require no configuration migration.

No container publication is implied by the version or changelog update. Keep pull examples on
already-published image tags until approved native image validation and publication finish.

## One version, automatic release notes, approved images

`<Version>` in [the application project](../src/MockAPI/MockAPI.csproj) is the only application-version
source. [package.json](../package.json) is private, unversioned development tooling.
Configuration `schemaVersion` is independent. Use SemVer without build metadata so the same version
is a valid immutable Docker tag.

Tests verify application-version presence, SemVer format, and metadata-derived consistency, not a
hard-coded current release. A version bump must not require test updates. Fixed synthetic versions
in parser, ordering, and release-policy tests are independent fixtures and remain unchanged.

1. **Validate, tag, and release:** merge an approved version bump and its reviewed
   [changelog entry](../CHANGELOG.md) to `main`. The final
   [quality job](../.github/workflows/quality.yml) waits for coverage, dependency, smoke, and complete
   browser checks before creating an annotated `v<Version>` tag at that exact commit and publishing
   a GitHub release with that version's Keep a Changelog notes. Prerelease versions are marked as
   prereleases and never marked as latest.
2. **Approve and publish images:** manually run [Publish approved release](../.github/workflows/container-release.yml)
   from `main`, supply that existing tag, and explicitly approve publication.
   Both native images are built from the resolved tag commit, never the current branch tip.
   The workflow attaches evidence to the existing GitHub release without replacing its notes.

Ordinary commits with an unchanged version do not create or move tags. A repeated tagging run
is safe only at the same commit. Downgrades and reused tags fail. Existing historical tags such
as `1.0.0-beta.3` remain untouched; new automation uses the `v` prefix. Introducing the automation
does not bump the application or retroactively tag an unchanged version.

The `GITHUB_TOKEN` tag push and release creation intentionally do not trigger another workflow.
The release is created directly in the final quality job, not by a tag-triggered workflow.
There is no automatic container push or container `latest` alias. For explicit recovery or
first-time tagging, manually run the complete **Quality engineering** workflow on `main`;
it validates, tags, and releases that commit. Do not use this to replace an already tagged version.

Use the [versioning skill](../.github/skills/versioning/SKILL.md) and
[versioning agent](../.github/agents/versioning.agent.md) for compatibility decisions.
Creating the `v1.4.0` tag still requires the approved application-version bump to reach `main`.

## Reviewed changelog contract

Maintain [CHANGELOG.md](../CHANGELOG.md) with human-readable changes, not a raw commit log.
Before merging a version bump, move the relevant changes from `[Unreleased]` into exactly one
`## [<Version>] - YYYY-MM-DD` entry matching the project version. Use the planned release date;
automation does not rewrite the date or commit changes back to the repository.

Use only the Keep a Changelog categories **Added**, **Changed**, **Deprecated**, **Removed**,
**Fixed**, and **Security**. Each included category must have a nonempty Markdown bullet list;
omit unused categories. Keep `[Unreleased]` for future work and maintain comparison links.
The changelog is release history, not a second application-version source.

Validation rejects missing or duplicate entries, invalid dates, unknown or repeated categories,
and empty change lists before a new tag is created. GitHub notes contain only the selected
version's entry and the changelog's reference-link definitions, never the unreleased or older
change lists. A rerun leaves a matching release and its assets untouched; mismatched metadata
fails rather than overwriting a published release.

Review the public-source audit before merging the bump: the automatic GitHub release also
exposes GitHub's source archives. Merging the reviewed version and changelog is the approval
boundary for GitHub metadata, not approval to publish container images.

## Repository setup (once, by an administrator)

Container publication and Pages deployment are disabled until enabled explicitly. GitHub
release metadata is automatic after a validated version bump on `main`. Do not store registry
credentials in the repository, development container, or Codespaces settings.

| Setting              | Required value or action                                                                                                                                |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Actions permissions  | Permit the final quality job's `contents: write` for tags and GitHub releases; allow immutable tag creation under the repository's tag rules            |
| Release assets       | Permit post-publication evidence uploads; GitHub's immutable-release setting is incompatible with this two-step asset workflow                          |
| Release environment  | Create `release`; limit deployment branches to `main`; require reviewers and prevent self-review when supported                                         |
| Registry credentials | Add `DOCKERHUB_USERNAME` and a least-privilege `DOCKERHUB_TOKEN` to the `release` environment (repository secrets only if environments are unavailable) |
| Release opt-in       | Set repository variable `ENABLE_RELEASE_PUBLISHING=true` only after the audit and approvals are arranged                                                |
| Pages source         | Choose **GitHub Actions** under Settings > Pages, after reviewing what will become public                                                               |
| Pages environment    | Protect `github-pages` and restrict deployments to `main`                                                                                               |
| Pages opt-in         | Set repository variable `ENABLE_PAGES=true` only when publication is approved                                                                           |

GitHub may require a paid plan for protected environments, branch protection, or Pages on a
private repository. Until required reviewers are available, the manual workflow's explicit
approval checkbox is the human gate, not an independent two-person approval.
Leave publishing disabled if that is insufficient for your governance requirements.
Do not change repository visibility solely to make a workflow pass.

GitHub immutable releases lock their assets at publication. If that setting is required,
this automatic-metadata/separately-approved-image sequence needs a different evidence delivery
design before container publication can be enabled. Image publication rejects an immutable
GitHub release before any registry push; it never disables repository protections automatically.
The pipeline independently refuses to move tags, overwrite notes, or replace existing assets.

## Release checklist

1. Review compatibility, known limitations, documentation, the proposed SemVer increment, and
   its dated Keep a Changelog entry.
2. Run the [public-release audit](../.github/skills/public-release-audit/SKILL.md) across the tracked
   tree and **all reachable Git history**. Inspect intended source, image, site, and SBOM contents.
   Never treat a source grep as a secret-history scan.
3. Run focused version tests and the canonical lint action:

   ```text
   pnpm run validate:version
   node scripts/release-notes.cjs check
   pnpm exec vitest run tests/frontend/release-version.test.js tests/frontend/release-notes.test.js
   ```

   ```powershell
   .\start.ps1 -Action lint
   ```

   ```bash
   ./start.sh --action lint
   ```

4. Merge the approved version bump and changelog. Wait for **Quality engineering**, including
   the tag/release job, to succeed. Confirm the GitHub release contains the reviewed notes.
   A failed quality workflow must not be bypassed by manually creating a tag.
5. Dispatch **Publish approved release** from `main`. Enter the exact `v<Version>` tag and approve
   publication only after reviewing the audit. No credentials are needed for the native build jobs.
6. Review both architecture checks, then approve the `release` environment if configured.
7. Confirm Docker Hub index membership and attached release evidence. Only then update
   README pull examples to the newly available tag. Keep prereleases marked as prereleases.

The workflow requires successful quality evidence for the exact commit, verifies the project/tag
match before building, tests native AMD64 and ARM64 images, generates SBOMs, and blocks all HIGH
and CRITICAL scan findings, including unfixed findings. It transfers the exact tested images
between jobs, verifies configuration digests, and assembles the multi-platform OCI index without
rebuilding. See [container validation](CONTAINERS.md#multi-architecture-release-boundary).

GitHub release assets retain SBOMs, scan output, per-architecture image/container metadata,
resource observations, and the published index beyond the 14-day Actions artifact retention.
The release workflow does not itself certify the public-source audit, cold-start regression
budget, or SBOM disclosure review: those remain human release gates.

## Failure and recovery

- **Version unchanged:** expected on non-version pushes. Make a deliberate new version bump
  and changelog entry for the next release; do not retag routine commits.
- **Invalid or missing changelog entry:** correct the reviewed entry before merging the bump.
  Validation failure does not create a tag.
- **Tag pushed, GitHub release failed:** rerun the failed tag/release job from the original
  quality run. It reuses only the same tag at the same commit and retries the missing release.
  Authentication, API, and network errors fail explicitly; they are never treated as missing releases.
- **Existing release differs:** investigate the commit, prerelease flag, draft state, or notes.
  Automation refuses to rewrite it. Published changes require a new version or an explicitly
  reviewed metadata correction outside this pipeline.
- **Legacy tag has no changelog release:** do not dispatch image publication until the matching
  reviewed GitHub release has been prepared. This pipeline does not retrofit historical releases.
- **Force-push replaces the previous commit:** tagging fetches the pre-push commit explicitly
  when it is no longer in the checkout's reachable history, then compares versions normally.
  Unchanged versions remain no-ops; downgrades and tag reuse still fail. If the remote can no
  longer provide the previous commit, tagging fails explicitly rather than assuming a new version.
- **Existing tag points elsewhere:** choose a new version. Never delete, move, or force-push it.
- **No matching quality evidence:** run quality validation for the tagged commit through the
  normal `main` workflow. Investigate expired evidence or legacy tags rather than bypassing the gate.
- **Native build, persistence, or scan failure:** no container image reaches the release tag;
  the earlier GitHub changelog release remains. Fix the cause;
  approve any time-bounded vulnerability exception separately rather than ignoring unfixed CVEs.
- **Partial registry push:** run-specific candidate tags may remain, but are not release tags.
  Remove only confirmed orphaned candidates through registry administration.
- **Image published, evidence upload failed:** do not rerun blindly or overwrite the image.
  Verify its index and configuration digests against the retained artifacts, then complete only
  the missing release assets with explicit maintainer approval. A new image needs a new version.

## Pages publication

The [landing page](../site/index.html) is static documentation, not a public MockAPI service.
It initializes Google Analytics 4 directly with measurement ID `G-XQZ0DQP020`, only on
`https://mockapi.simondoescloud.com`. Local previews and alternate hosts do not load analytics.
The previous GTM container `GTM-N92H54N6` had an empty published configuration (no tags or rules),
so loading it did not send GA4 page views. Do not add GTM alongside the direct Google tag:
configuring the same GA4 destination in both can double-count page views.
The tag queues one automatic page view, excludes query strings, fragments, and referrer values,
disables Google signals and advertising personalization, and defaults advertising consent to denied.
Global Privacy Control, Do Not Track, and `window["ga-disable-G-XQZ0DQP020"] = true` prevent loading.
Analytics is not included in the runtime dashboard or mock endpoints.
All other assets are self-hosted, with no externally loaded fonts.
Before publishing or changing analytics, review applicable privacy-disclosure and consent
requirements; eligible visitors have analytics storage granted without a consent banner.
Browser opt-outs are not a substitute for consent where prior consent is required.
[The build](../scripts/build-site.cjs) copies only six approved assets to `artifacts/site`;
unknown output files fail the build. Never upload the repository root or the whole `docs` directory.

```text
pnpm run build:site
pnpm run test:site
node scripts/serve-site.cjs
```

Preview at `http://127.0.0.1:4173/MockAPI/`. The tests exercise this repository prefix, asset loading,
keyboard navigation, desktop/mobile layouts, light/dark automated accessibility, and SEO metadata
without JavaScript. Browser tests stub the Google tag to verify initialization without sending
test traffic to Google. The allowlisted `robots.txt` and `sitemap.xml` provide crawler discovery.
After deployment, use a browser without tracking blockers or privacy opt-outs and verify that
`gtag/js?id=G-XQZ0DQP020` loads and a `google-analytics.com/g/collect` request contains
`tid=G-XQZ0DQP020` and `en=page_view`. Confirm the visit in the matching GA4 property's Realtime
report; check property data filters if requests succeed but the visit is absent.
Stubbed tests verify site initialization, not Google ingestion or access to the GA4 property.
HTML and structured-data author metadata credit Simon Kurtz.
After enabling Pages and its opt-in variable, dispatch **Documentation site** or push a site change
to `main`. The canonical public address is `https://mockapi.simondoescloud.com/`; keep this custom
domain configured in Settings > Pages so the repository's Pages URL redirects to it.
Keep the canonical link, Open Graph and Twitter metadata, JSON-LD, robots sitemap directive,
and sitemap URLs aligned when changing the domain. Relative assets still support repository-prefix previews.
PRs build and test but never deploy. Local changes are not live until the approved Pages deployment completes.

After deployment, verify `/robots.txt` and `/sitemap.xml` return `200` on the custom domain, then submit
`https://mockapi.simondoescloud.com/sitemap.xml` through Google Search Console and Bing Webmaster Tools
using a verified domain owner account. Structured data describes the website and software without
inventing ratings or reviews; it does not guarantee a rich result or search ranking.
