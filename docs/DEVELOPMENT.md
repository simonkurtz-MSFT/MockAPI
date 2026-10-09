# Codespaces and Development Containers

Use [Create a codespace](https://codespaces.new/simonkurtz-MSFT/MockAPI) for a browser-based workspace,
or install VS Code's Dev Containers extension and choose **Dev Containers: Reopen in Container**
from a local checkout. Local Dev Containers require a supported Docker engine; Codespaces does not
require Docker on your machine. Repository access, Codespaces availability, and usage charges depend
on your GitHub account or organization.

## First run

Wait for the post-create setup to finish. It uses the digest-pinned .NET 10 development image,
installs exact Node.js `26.9.0` through the image's nvm, installs the pnpm version declared in
`package.json`, and restores the frozen JavaScript lockfile and .NET solution.
The setup does not install Playwright browsers or require a container engine inside the workspace.

```powershell
.\start.ps1 -Action run-tutorial
```

```bash
./start.sh --action run-tutorial
```

Open **port 5080** in the VS Code **Ports** panel. In Codespaces, use the forwarded HTTPS address,
not `localhost` on your own computer. Keep port visibility **Private**; Codespaces private port
forwarding is the access boundary. The `devcontainer.json` port configuration only controls
forwarding and notification; GitHub's private default and your visibility settings control access.
Do not change it to Public without intentionally configuring application authentication and HTTPS.

Select **Load examples**, configure administrator credentials, and generate a key in **Settings > Mock API
security** before testing `/ctp/parks`. Dashboard tests attach the memory-only key automatically; external
requests require `X-MockAPI-Key`. Edit a response; changes are saved automatically. Use **Retry save**
if persistence fails. See
[Mock API keys](OPERATIONS.md#mock-api-keys) for bootstrap and persistence.
Saves go to the ignored `artifacts/local-data/mockapi.json` inside the workspace.
Rebuilding the development container does not overwrite that file.
Deleting the codespace deletes workspace-local data: export anything you need to keep first.
Stop MockAPI with `Ctrl+C`.

## Validation

The image includes PowerShell, so both developer entry points work.

```powershell
.\start.ps1 -Action build
.\start.ps1 -Action test
.\start.ps1 -Action lint
```

```bash
./start.sh --action build
./start.sh --action test
./start.sh --action lint
```

For dashboard or site browser tests, install the browser matching the repository's pinned Playwright:

```text
pnpm exec playwright install --with-deps chromium
pnpm run test:site
pnpm run test:browser:smoke
```

`pnpm run validate:dependency-security` audits pnpm and direct and transitive NuGet
dependencies. Dependency findings are warning-only: the reports remain visible,
but builds and validation continue even when vulnerabilities are found.
Scanner execution failures and invalid reports still fail validation.
This policy does not remediate vulnerabilities or change container-image scanning.

The [development-container workflow](../.github/workflows/devcontainer.yml) exercises the exact image,
non-root setup, tooling, and application readiness. A real Codespaces launch still needs account-level
validation. No production credentials, Docker socket, or Azure login is needed for ordinary development.

## Maintenance and boundaries

- The development image is deliberately separate from the small Native AOT runtime image.
- Keep the image digest and exact Node version age-verified with `pnpm run validate:dependency-age`.
  Node `26.10.0` had not completed the eight-day cooldown when this setup was introduced.
- The image supports Linux AMD64 and ARM64. Its SDK follows `global.json`; no extra SDK pin is introduced.
- Setup uses `pnpm install --frozen-lockfile` and the CLI's `restore` action rather than the full
  `setup` action, which also checks for a local container engine.
- A local Dev Containers forwarded port binds locally; Codespaces is private by default. Neither
  replaces the application's authentication requirements for a shared deployment.
- Container release builds remain on matching native CI runners. This workspace does not prove
  either release architecture, read-only operation, SBOM cleanliness, or release readiness.

If setup reports an npm TLS handshake failure, check the container's registry connectivity and
your organization's proxy/CA requirements. Host connectivity alone does not prove the container
can reach the registry. Do not disable TLS certificate verification or commit proxy credentials;
correct the environment, then rerun `bash .devcontainer/setup.sh`.
