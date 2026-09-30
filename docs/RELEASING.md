# Versioning and Releases

MockAPI's stable 1.0.0 application version is approved. This version decision does not publish a
release or replace the quality, public-release audit, native image validation, and publication
approval gates below.

## One version, two deliberate steps

`<Version>` in [the application project](../src/MockAPI/MockAPI.csproj) is the only application-version
source. [package.json](../package.json) is private, unversioned development tooling.
Configuration `schemaVersion` is independent. Use SemVer without build metadata so the same version
is a valid immutable Docker tag.

1. **Validate and tag:** merge an approved version bump to `main`. The final
   [quality job](../.github/workflows/quality.yml) waits for coverage, dependency, smoke, and complete
   browser checks before creating an annotated `v<Version>` tag at that exact commit.
2. **Approve and publish:** manually run [Publish approved release](../.github/workflows/container-release.yml)
   from `main`, supply that existing tag, and explicitly approve publication.
   Both native images are built from the resolved tag commit, never the current branch tip.

Ordinary commits with an unchanged version do not create or move tags. A repeated tagging run
is safe only at the same commit. Downgrades and reused tags fail. Existing historical tags such
as `1.0.0-beta.3` remain untouched; new automation uses the `v` prefix. Introducing the automation
does not bump the application or retroactively tag an unchanged version.

The `GITHUB_TOKEN` tag push intentionally does not trigger another workflow. There is no automatic
release, container push, or `latest` alias. For explicit recovery or first-time tagging, manually
run the complete **Quality engineering** workflow on `main`; it validates and tags that commit.
Do not use this to replace an already tagged version.

Use the [versioning skill](../.github/skills/versioning/SKILL.md) and
[versioning agent](../.github/agents/versioning.agent.md) for compatibility decisions.
Creating the final `v1.0.0` tag still requires an approved application-version bump.

## Repository setup (once, by an administrator)

Publication and Pages deployment are disabled until enabled explicitly. Do not store registry
credentials in the repository, development container, or Codespaces settings.

| Setting              | Required value or action                                                                                                                                |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Actions permissions  | Permit the quality tagging job's `contents: write`; allow immutable tag creation under the repository's tag rules                                       |
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

## Release checklist

1. Review compatibility, known limitations, documentation, and the proposed SemVer increment.
2. Run the [public-release audit](../.github/skills/public-release-audit/SKILL.md) across the tracked
   tree and **all reachable Git history**. Inspect intended source, image, site, and SBOM contents.
   Never treat a source grep as a secret-history scan.
3. Run focused version tests and the canonical lint action:

   ```text
   pnpm run validate:version
   pnpm exec vitest run tests/frontend/release-version.test.js
   ```

   ```powershell
   .\start.ps1 -Action lint
   ```

   ```bash
   ./start.sh --action lint
   ```

4. Merge the approved version bump. Wait for **Quality engineering**, including the tag job,
   to succeed. A failed quality workflow must not be bypassed by manually creating a tag.
5. Dispatch **Publish approved release** from `main`. Enter the exact `v<Version>` tag and approve
   publication only after reviewing the audit. No credentials are needed for the native build jobs.
6. Review both architecture checks, then approve the `release` environment if configured.
7. Confirm Docker Hub index membership, release notes, and attached evidence. Only then update
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
  for the next release; do not retag routine commits.
- **Existing tag points elsewhere:** choose a new version. Never delete, move, or force-push it.
- **No matching quality evidence:** run quality validation for the tagged commit through the
  normal `main` workflow. Investigate expired evidence or legacy tags rather than bypassing the gate.
- **Native build, persistence, or scan failure:** nothing reaches the release tag. Fix the cause;
  approve any time-bounded vulnerability exception separately rather than ignoring unfixed CVEs.
- **Partial registry push:** run-specific candidate tags may remain, but are not release tags.
  Remove only confirmed orphaned candidates through registry administration.
- **Image published, GitHub release failed:** do not rerun blindly or overwrite the image.
  Verify its index and configuration digests against the retained artifacts, then complete only
  the missing release metadata/assets with explicit maintainer approval. A new image needs a new version.

## Pages publication

The [landing page](../site/index.html) is static documentation, not a public MockAPI service.
It uses Google Analytics with measurement ID `G-XQZ0DQP020` through Google's asynchronous `gtag.js`.
This tag loads automatically on the documentation page, including local previews unless blocked;
it is not included in the runtime dashboard or mock endpoints.
All other assets are self-hosted, with no externally loaded fonts.
Before publishing, review applicable privacy-disclosure and consent requirements; the supplied tag
does not implement a consent banner or consent gating.
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
