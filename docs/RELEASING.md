# Versioning and Releases

MockAPI's next stable application version is 1.8.0. This version decision publishes only after
the quality, public-release audit, and native image validation gates below succeed.

## 1.8.0 release summary

The planned release date is **2026-10-21**, with the immutable tag **`1.8.0`**.
The [1.8.0 changelog entry](../CHANGELOG.md#180---2026-10-21) supplies the GitHub release notes.

- Dashboard event streams accept `deltas=true` for sequenced, absolute statistics state patches
  after a full baseline. Resets, reconnects, and missed frames receive full baselines.
- Configuration streaming, immutable response reuse, buffered JSON validation, API-key verification,
  and shared statistics snapshots and serialization reduce allocation work.
- Dashboard statistics retain rows and chart columns across updates to preserve focus and timeline
  scroll. The documentation site adds APIM sample guidance and refines its presentation.

### Compatibility and upgrade behavior

The optional statistics-delta contract is additive, so 1.8.0 is a minor release. Omitting `deltas`
or setting it to `false` preserves the original full dashboard statistics events without SSE IDs.
Statistics GET responses and the standalone statistics stream retain their existing contracts.
The bundled dashboard opts into deltas and resynchronizes when its baseline is no longer valid.
Existing saved endpoint IDs, mock routing, management writes, authentication, security persistence,
and configuration schema (`schemaVersion: "1.0"`) remain compatible; no migration is required.
Configuration replacements still create fresh rate-limit permits, and statistics retain their
existing counting, bounded retention, and reset semantics. Allocation reductions do not guarantee
lower resident memory in every environment. Keep pull examples on published image tags until
1.8.0 image validation and publication finish.

## 1.7.0 historical release summary

The planned release date is **2026-10-21**, with the immutable tag **`1.7.0`**.
The [1.7.0 changelog entry](../CHANGELOG.md#170---2026-10-21) supplies the GitHub release notes.

- Deployments can supply the initial mock API key through `MockApi__ApiKey`, including Azure
  infrastructure and developer CLI integration. Startup persists only its SHA-256 hash before
  enabling protection, and existing security settings take precedence.
- Configuration and bulk selection no longer cap endpoint counts at 25. The 4 MiB document limit
  and independent 25-group API-description limit remain.
- Built-in examples combine 41 Trailhead Supply endpoints across assistant, inventory, and shipping
  APIs, with standalone native sample files and corresponding illustrative request payloads.
- Dashboard API groups support filtered group selection and bulk collapse or expansion. Operations
  default to path order, and information previews improve sample-description readability.

### Compatibility and upgrade behavior

These additions are backward compatible, so 1.7.0 is a minor release. Existing saved endpoint IDs,
mock routing, management routes, credential configuration, and configuration schema
(`schemaVersion: "1.0"`) remain compatible; no migration is required. Documents with more than
25 endpoints require MockAPI 1.7.0 or later because older binaries enforce the previous limit.
The new sample files replace the former checked-in sample filenames, not the persisted instance
filename, which remains `mockapi.json` by default. Update source-sample copy commands or paths
when adopting the Trailhead files. Upgrading does not activate examples or rewrite saved endpoints;
**Load examples** remains an explicit merge with conflict review before forced updates.
The optional deployment key seeds only missing security settings and never replaces an existing
key, rotation, or explicit protection opt-out. Existing deployments need not supply it.
Keep pull examples on published image tags until 1.7.0 image validation and publication finish.

## 1.6.2 historical release summary

The release date was **2026-10-15**, with the immutable tag **`1.6.2`**.
The [1.6.2 changelog entry](../CHANGELOG.md#162---2026-10-15) supplies the GitHub release notes.

- Valid Basic credentials establish a one-time, process-scoped secure dashboard session cookie
  instead of repeating PBKDF2 verification for every administrative request in the same browser
  session.
- Dashboard endpoint loading and test-response presentation are refined, including formatted JSON
  responses with continued access to raw response bodies.
- Deployment documentation consistently sets Azure Container Apps minimum and maximum replica
  counts to 1, and container release guidance documents the required Docker Hub token permissions.
- Matched mock `POST`, `PUT`, and `PATCH` requests accept optional bodies, enforce a 1 MiB limit,
  and validate non-empty JSON syntax and UTF-8 encoding before the configured response is applied.

### Compatibility and upgrade behavior

Valid mock requests, endpoint IDs, management API contracts, credential configuration, and the
configuration schema (`schemaVersion: "1.0"`) remain compatible, so 1.6.2 is a patch release with
no configuration migration. Matched mock `POST`, `PUT`, and `PATCH` requests with malformed JSON,
invalid content types, unsupported JSON charsets, or bodies over 1 MiB now receive `400`, `400`,
`415`, or `413` respectively instead of the configured response. Dashboard sessions remain
process-scoped, consistent with the documented single-replica deployment model.

## 1.6.1 historical release summary

The release date was **2026-10-14**, with the immutable tag **`1.6.1`**.
The [1.6.1 changelog entry](../CHANGELOG.md#161---2026-10-14) supplies the GitHub release notes.

- Native AOT OpenAPI generation includes endpoint-selection query parameter metadata.
- The runtime image includes the Alpine zlib security fix in .NET runtime-deps 10.0.13.
- Container-only validation is manually dispatchable, and release recovery no longer repeats the
  full quality baseline after an immutable tag and matching GitHub release exist.
- Quality retries recover a missing immutable tag when the version was already changed by an
  earlier commit whose validation failed before tagging.

### Compatibility and upgrade behavior

These fixes do not change mock routing, management API contracts, authentication behavior, or the
configuration schema (`schemaVersion: "1.0"`), so 1.6.1 is a patch release. Existing endpoint IDs
and configuration documents remain compatible; no migration is required.

## 1.6.0 release summary

The planned release date is **2026-10-14**, with the immutable tag **`1.6.0`**.
The [1.6.0 changelog entry](../CHANGELOG.md#160---2026-10-14) supplies the GitHub release notes.

- Dashboard and management API exports can select enabled endpoints by stable endpoint ID in
  native configuration and portable request/test formats.
- Request statistics consistently record every mock-dispatch attempt, including failures.
- Metadata-only configuration reads avoid parsing endpoint definitions.

### Compatibility and upgrade behavior

Selective export is additive: omitting `endpointId` retains full-export behavior. Existing endpoint
IDs, configuration documents, management routes, and configuration schema (`schemaVersion: "1.0"`)
remain compatible; no migration is required. Statistics continue to use the same response fields,
but counts include failed transport attempts as well as completed responses.

## 1.5.2 historical release summary

The release date was **2026-10-14**, with the immutable tag **`1.5.2`**.
The [1.5.2 changelog entry](../CHANGELOG.md#152---2026-10-14) supplies the GitHub release notes.

- Dashboard endpoint table presentation and editing guidance are refined.
- CI uses pinned Ubuntu 26.04 runners and pnpm 12.10.0.
- Container publication validates exact-commit quality evidence and rejects an existing release
  image tag before publication.

### Compatibility and upgrade behavior

These dashboard and release-pipeline changes do not change mock routing, the management API,
authentication behavior, or the configuration schema (`schemaVersion: "1.0"`), so 1.5.2 is a
patch release. Existing endpoint IDs and configuration documents remain compatible; no migration
is required.

## 1.5.0 historical release summary

The release date was **2026-10-13**, with the immutable tag **`1.5.0`**.
The [1.5.0 changelog entry](../CHANGELOG.md#150---2026-10-13) supplies the GitHub release notes.

- Mock responses and intentional connection aborts support an optional bounded,
  cancellation-aware delay.
- Public-site links and application-version presentation are refined.
- Container publication resolves pushed architecture-candidate digests unambiguously.

### Compatibility and upgrade behavior

These additions are backward compatible, so 1.5.0 is a minor release. Existing endpoint IDs,
configuration documents, management API behavior, authentication behavior, and the configuration
schema (`schemaVersion: "1.0"`) remain compatible; no migration is required. Configurations that
use the new `delaySeconds` field require MockAPI 1.5.0 or later because older binaries reject
unknown configuration fields.

## 1.4.4 historical release summary

The release date was **2026-10-13**, with the immutable tag **`1.4.4`**.
The [1.4.4 changelog entry](../CHANGELOG.md#144---2026-10-13) supplies the GitHub release notes.

- Container validation tolerates transient readiness request failures after recreation.
- Manual container release recovery recognizes successful quality validation of the immutable tag
  and provides an actionable command when validation evidence is missing.

### Compatibility and upgrade behavior

These release-pipeline fixes do not change mock routing, the management API, runtime behavior, or
the configuration schema (`schemaVersion: "1.0"`), so 1.4.4 is a patch release. Existing endpoint
IDs and configuration documents remain compatible; no migration is required.

## 1.4.3 historical release summary

The release date was **2026-10-13**, with the immutable tag **`1.4.3`**.
The [1.4.3 changelog entry](../CHANGELOG.md#143---2026-10-13) supplies the GitHub release notes.

- The built-in example configuration gained additional endpoints and clearer descriptions.
  That sample has since been replaced by Trailhead Supply.
- Dashboard layout and endpoint testing interactions are refined.

### Compatibility and upgrade behavior

These example and dashboard refinements do not change mock routing, the management API, or the
configuration schema (`schemaVersion: "1.0"`), so 1.4.3 is a patch release. Existing endpoint IDs
and configuration documents remain compatible; no migration is required.

## 1.4.2 historical release summary

The release date was **2026-10-12**, with the immutable tag **`1.4.2`**.
The [1.4.2 changelog entry](../CHANGELOG.md#142---2026-10-12) supplies the GitHub release notes.

- Dashboard and documentation-site copy, formatting, and styling are refined, with a clearer
  documentation-site feature overview.
- Container publication follows the quality workflow's validated version tag and GitHub release
  automatically; manual dispatch remains available for recovery of an existing validated tag.
- Azure Container Apps deployments keep one replica running instead of scaling to zero.

### Compatibility and upgrade behavior

The presentation refinements, publication workflow changes, and Azure minimum-replica correction
do not change the management API, mock routing, persisted endpoint configuration, or configuration
schema (`schemaVersion: "1.0"`), so 1.4.2 is a patch release. Existing endpoint IDs, configuration
documents, API-key enforcement, and stored UTC statistics remain unchanged. No configuration
migration is required. Azure deployments retain one running replica and its associated baseline cost.

The validated version tag starts container publication after the GitHub release is created. Keep
pull examples on already-published image tags until native image validation and publication finish.

## One version, automatic release notes and images

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
   browser checks before creating an annotated `<Version>` tag at that exact commit and publishing
   a GitHub release titled `v<Version>` with that version's Keep a Changelog notes. Prerelease
   versions are marked as prereleases and never marked as latest.
2. **Validate and publish images:** for a new version, the quality workflow calls
   [Publish release container](../.github/workflows/container-release.yml) with the validated tag.
   Both native images are built from the resolved tag commit, never the current branch tip.
   The workflow attaches evidence to the existing GitHub release without replacing its notes.

Ordinary commits with an unchanged version do not create or move tags. A repeated tagging run
is safe only at the same commit. When no matching tag exists because an earlier validation failed
before tagging, the next successful quality run creates the missing tag even if the version did not
change in that retry commit. Downgrades and reused tags fail. Existing historical tags,
including tags with a `v` prefix, remain untouched; new automation uses exact `<Version>` tags
without a prefix. Changing the convention does not bump the application or retroactively retag
an unchanged version.

The `GITHUB_TOKEN` tag push does not trigger another workflow, so the final quality job explicitly
calls the reusable container workflow after creating the tag and GitHub release. There is no
container `latest` alias. For recovery, manually run **Publish release container** with the existing
validated tag. The immutable tag and matching GitHub release are durable proof that the quality
workflow validated that commit before release creation; recovery does not repeat the coverage and
browser baseline. Do not use either workflow to replace an already published tag.

To exercise a container fix before selecting a new release version, manually run **Container pull
request validation** from the branch containing the fix. That workflow builds and validates both
native architectures without running the separate coverage and browser jobs. A source fix made
after a release tag still requires a new patch version because published tags are immutable.

Dispatch **Publish release container** from `main`, supplying the immutable release tag as its
input so recovery uses the current workflow fixes while building the exact tagged source.
Preflight verifies the tagged GitHub release and Docker Hub credentials before native builds.
It also checks Docker daemon availability and rejects an existing container release tag or a
failed registry lookup before compilation. Publication rechecks tag absence after native
validation so another publisher cannot silently replace an immutable release.
Each architecture is uploaded with a run-scoped candidate tag while its manifest and configuration
digests are verified. The workflow attempts to remove those temporary tags after both successful
and failed publication attempts. Cleanup is best-effort: failures remain visible in the workflow
logs but do not fail publication or prevent release evidence attachment. Failed deletions emit
workflow warnings; leftover candidate tags may require manual removal. Only the exact
application-version tag is a supported release tag.
Both native runners check Docker, PowerShell 7, curl, and jq availability before compilation;
the AMD64 and ARM64 hosted runner images supply those tools. Missing tools fail explicitly.
Native validation and scan evidence is uploaded even on failure; publication still requires
both native jobs to succeed. Quality validation runs the native publication contract tests
before tagging.

Use the [versioning skill](../.github/skills/versioning/SKILL.md) and
[versioning agent](../.github/agents/versioning.agent.md) for compatibility decisions.
The historical `1.4.3` tag remains immutable. The next approved application-version bump uses
the exact project version as its tag without a prefix.

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

Container publication and GitHub release metadata are automatic after a validated version bump on
`main`. Pages deployment remains disabled until enabled explicitly. Do not store registry
credentials in the repository, development container, or Codespaces settings.

| Setting              | Required value or action                                                                                                                     |
| -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| Actions permissions  | Permit the final quality job's `contents: write` for tags and GitHub releases; allow immutable tag creation under the repository's tag rules |
| Release assets       | Permit post-publication evidence uploads; GitHub's immutable-release setting is incompatible with this two-step asset workflow               |
| Registry credentials | Add `DOCKERHUB_USERNAME` and a `DOCKERHUB_TOKEN` with Read, Write, Delete permissions as repository secrets                                  |
| Pages source         | Choose **GitHub Actions** under Settings > Pages, after reviewing what will become public                                                    |
| Pages environment    | Protect `github-pages` and restrict deployments to `main`                                                                                    |
| Pages opt-in         | Set repository variable `ENABLE_PAGES=true` only when publication is approved                                                                |

GitHub may require a paid plan for branch protection or Pages on a private repository. Do not
change repository visibility solely to make a workflow pass.

GitHub immutable releases lock their assets at publication. If that setting is required,
this automatic metadata-and-image sequence needs a different evidence delivery
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
   tag, release, and container publication, to succeed. Confirm the GitHub release contains the reviewed notes.
   A failed quality workflow must not be bypassed by manually creating a tag.
5. Confirm both architecture checks, Docker Hub index membership, and attached release evidence.
   If automatic publication fails after tag creation, fix the cause and rerun **Quality engineering**
   or manually dispatch **Publish release container** with the existing tag.
6. Only then update
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
- **No matching quality evidence:** run `gh workflow run quality.yml --ref <Version>` and wait
  for all three quality prerequisite jobs to succeed in a completed run. Retry container
  publication from `main`. A failed earlier container job alone does not require repeating quality
  validation. Investigate expired evidence or legacy tags rather than bypassing the gate.
- **Missing, invalid, or insufficient Docker Hub credentials:** configure `DOCKERHUB_USERNAME`
  and a `DOCKERHUB_TOKEN` with Read, Write, Delete permissions as repository secrets, then retry.
  Read and Write permit image publication; Delete is required to remove the run-specific candidate
  tags afterward. Preflight authenticates before native builds, but registry write and delete
  authorization are exercised only when pushing and cleaning up the validated candidate images.
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
