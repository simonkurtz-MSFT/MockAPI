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

Before each commit, inspect both staged and unstaged changes because some files may already contain user-staged work. Do not use a blanket reset or checkout.