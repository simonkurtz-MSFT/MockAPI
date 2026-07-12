# Commit Ledger

No commits are created automatically. Use this ledger to stage and review the completed work as three logical commits.

## 1. Runtime and dashboard features

Suggested subject: `feat: complete configuration management and endpoint testing`

- `src/MockAPI/Program.cs`
- `src/MockAPI/Management/ConfigurationManagementService.cs`
- `src/MockAPI/Management/ManagementApiEndpoints.cs`
- `src/MockAPI/Management/ManagementContracts.cs`
- `src/MockAPI/wwwroot/index.html`
- `src/MockAPI/wwwroot/app.js`
- `src/MockAPI/wwwroot/app.css`
- `tests/MockAPI.Tests/Management/ManagementApiTests.cs`
- `tests/MockAPI.Tests/Management/RemainingManagementApiTests.cs`

Includes non-destructive built-in merging, conflict preview and force semantics, management rate limiting, four examples, status filtering, and the endpoint test blade.

## 2. Developer and release automation

Suggested subject: `build: add showcase and container release validation`

- `start.ps1`
- `.dockerignore`
- `compose.yaml`
- `.github/dependabot.yml`
- `.github/workflows/container-pr.yml`
- `.github/workflows/container-release.yml`

Includes the example showcase, isolated container workflow support, dependency cooldowns, multi-architecture build evidence, SBOM generation, vulnerability scanning, and disabled-by-design publishing.

## 3. Operator documentation and plan status

Suggested subject: `docs: complete operating and release guidance`

- `README.md`
- `docs/CONFIGURATION.md`
- `docs/MANAGEMENT_API.md`
- `docs/OPERATIONS.md`
- `docs/TROUBLESHOOTING.md`
- `docs/PLAN.md`
- `docs/COMMIT_PLAN.md`

## 4. Quality engineering and minimal publish enforcement

Suggested subject: `test: add coverage accessibility and browser quality gates`

- `package.json`
- `pnpm-lock.yaml`
- `pnpm-workspace.yaml`
- `vitest.config.js`
- `playwright.config.js`
- `.github/mockapi-mark.svg`
- `.github/social-preview.svg`
- `.github/social-preview.png`
- `src/MockAPI/wwwroot/dashboard-core.js`
- `src/MockAPI/wwwroot/favicon.svg`
- `tests/frontend/dashboard-core.test.js`
- `tests/browser/global-setup.js`
- `tests/browser/dashboard.spec.js`
- `tests/coverage.runsettings`
- `scripts/Assert-Coverage.ps1`
- `scripts/Assert-PublishContents.ps1`
- `scripts/Test-DependencyAge.ps1`
- `scripts/Test-WorkflowPins.ps1`
- `.github/dependency-age-exceptions.json`
- `.github/workflows/quality.yml`
- `.github/workflows/container-pr.yml`
- `.github/workflows/container-release.yml`
- `.github/dependabot.yml`
- `.dockerignore`
- `src/MockAPI/MockAPI.csproj`
- `src/MockAPI/Configuration/MockApiOptions.cs`
- `src/MockAPI/Program.cs`
- `src/MockAPI/wwwroot/app.js`
- `src/MockAPI/wwwroot/app.css`
- `src/MockAPI/wwwroot/index.html`
- `start.ps1`
- `tests/MockAPI.Tests/Configuration/ConfigurationStartupTests.cs`
- `tests/MockAPI.Tests/Configuration/ConfigurationFileStoreTests.cs`
- `tests/MockAPI.Tests/Configuration/ConfigurationStateTests.cs`
- `tests/MockAPI.Tests/Configuration/ConfigurationValidatorTests.cs`
- `tests/MockAPI.Tests/Management/ConfigurationManagementServiceTests.cs`
- `tests/MockAPI.Tests/Management/EndpointManagementServiceTests.cs`
- `tests/MockAPI.Tests/Management/ManagementApiTests.cs`
- `tests/MockAPI.Tests/Management/RemainingManagementApiTests.cs`
- `tests/MockAPI.Tests/Runtime/MockDispatcherTests.cs`
- `tests/MockAPI.Tests/Runtime/RequestStatisticsCollectorTests.cs`
- `docs/ACCESSIBILITY.md`
- `docs/PLAN.md`
- `docs/WSLC.md`
- `README.md`
- `.github/copilot-instructions.md`
- `.github/instructions/container.instructions.md`
- `.github/instructions/dotnet.instructions.md`

Includes seven-day dependency-age enforcement, source-only backend coverage gates, 100% deterministic frontend helper coverage, Playwright/axe projects and CI evidence, accessibility release guidance, symbol-free publications, minimized Docker context, and measured final image contents.

Before each commit, inspect both staged and unstaged changes because some Phase 7 files currently contain both staged and unstaged edits. Do not use a blanket reset, checkout, or indiscriminate `git add -A`.
