# Operations and Security

## Health and Exposure

- Liveness: `/health/live`
- Readiness: `/health/ready`
- Dashboard: `/`
- Management API: `/__mockapi/api`
- Live mock OpenAPI export for APIM: `/__mockapi/api/configuration/export/openapi`
- Management OpenAPI: `/__mockapi/openapi/v1.json`
- Swagger UI: `/__mockapi/swagger`

See [Live mock OpenAPI and APIM import](MANAGEMENT_API.md#live-mock-openapi-and-apim-import) for download links, backend URL configuration, and re-import requirements. The live mock export depends on the management API, not on the separate management OpenAPI or Swagger UI switches.

Optional HTTP Basic authentication protects all administrative surfaces when both `MockApi__DashboardUsername` and `MockApi__DashboardPasswordHash` are configured. This includes the dashboard and static assets, management API, OpenAPI document, and Swagger UI. Health remains public; mock calls require an API key by default. Missing pairs and malformed hashes fail startup.

### Mock API keys

Mock calls require `X-MockAPI-Key`. With no key generated, they return `401` before endpoint dispatch,
connection aborts, rate limiting, or mock statistics. Administrative credentials do not authorize mock calls.

1. Configure the dashboard administrator username and password hash using the existing deployment workflow.
2. Open **Settings > Mock API security > Dashboard test key > Generate key**.
3. Copy the generated key immediately. The server stores only its SHA-256 hash and cannot show it again.
4. Supply `X-MockAPI-Key` on external mock calls. Dashboard tests automatically use the key held in memory.
   After a reload, enter the existing key in Settings or generate a replacement.

Settings separates instance-wide **Request protection** from the memory-only **Dashboard test key**.
Changing the **Require X-MockAPI-Key on mock requests** checkbox automatically saves the protection setting
on the server for all callers, including after a restart. Disabling protection still requires confirmation;
a cancelled or failed save restores the checkbox to the saved setting.
When a key already exists, **Rotate key** replaces it after confirmation. **Copy key** is available only
when this page holds a valid key; the dashboard never retrieves an existing key from the server.
The status badge explains whether protection is on, off, awaiting a key, or unavailable without administrator setup.
Workspace preferences are separate and apply only to the current browser.

Security Settings require configured administrator credentials even when the rest of management is anonymous.
Without administrator credentials, security management returns `403`; ordinary mock calls still fail closed.
Leaving other management operations anonymous is not safe for an untrusted network: the key protects mock
invocation, not configuration integrity. Configure administrative authentication for shared deployments.

Rotation generates 32 random bytes, enables enforcement, and immediately revokes the old key. Changes require
the security settings' own strong ETag and are persisted before activation. Failed or stale writes leave active
authorization unchanged. Keys are never retained in browser storage, endpoint configuration, or statistics.
Endpoint tests reject redirects rather than forwarding a key to another target.

The hash and enforcement setting are saved in `<ConfigurationPath>.security.json`, or in a sibling
`<configuration-blob-name>.security.json` when Blob persistence is configured. Retain this document with the
configuration volume or private Blob container. Endpoint import/export/save never changes it. Losing this
document restores the fail-closed default; generate a replacement key as an administrator.

For a deliberately unauthenticated local fixture, set `MockApi__RequireApiKey=false` before the first security
document is saved. Persisted security settings take precedence over this bootstrap setting. Administrators can
also explicitly disable enforcement in Settings after confirming the exposure warning. Do not opt a shared or
public deployment out of protection.

Use HTTPS outside loopback, terminating TLS at the trusted host or ingress as described below. Neither API keys
nor Basic authentication encrypt transport or provide per-user identity, revocation, MFA, or abuse protection.
Keep secrets out of URLs, command history, logs, and source control. Anyone holding the shared key can invoke mocks.

Request exports use secret-free variables: `mockApiKey` in Postman and Insomnia, `MOCKAPI_KEY` for cURL,
k6 and HTTP files, and the JMeter `MOCKAPI_KEY` property. OpenAPI declares an API-key security scheme.
Fill the variable locally; do not share a populated export.

The supported WSLC creation workflow prompts for an optional username and reads the password and confirmation as secure strings. It stores only a random-salt PBKDF2-SHA256 hash in container configuration. Existing containers restart without prompting; recreate a container to change authentication while retaining its named data volume.

HTTP Basic authentication is sufficient for low-sensitivity administration on loopback or behind HTTPS with a strong unique password. It does not provide transport encryption, MFA, centralized identity, roles, audit history, or account lockout. Do not expose authenticated administrative surfaces over plain HTTP. For an internet-facing or sensitive deployment, keep them disabled or place them behind an identity-aware, rate-limited ingress.

MockAPI suppresses the Kestrel `Server` header. It does not log configured request or response bodies, authorization headers, cookies, or query values. Statistics and the newest-first feed of at most 100 request summaries are bounded, process-local, and reset on restart.

## Container Operation

The image runs as the built-in non-root `app` user on port `8080`. `/data` is its only writable application mount. The root filesystem is read-only compatible and the final image contains no shell or package manager.

The supported local workflow uses WSLC:

```powershell
.\start.ps1 -Action container-build
.\start.ps1 -Action container-run
.\start.ps1 -Action container-test
```

The container starts with a `0.5` CPU and `256 MiB` memory limit. Deployment manifests should request `0.25` CPU and `128 MiB`, with the same `0.5` CPU and `256 MiB` limits unless measured behavior justifies a change.

The included [compose.yaml](../compose.yaml) binds only to `127.0.0.1`, drops Linux capabilities, enables `no-new-privileges`, uses a read-only root filesystem, and retains configuration in the `mockapi-data` volume.

## TLS

Terminate TLS at the container host, reverse proxy, or ingress. Do not add certificates or TLS tooling to the runtime image.

For direct local .NET development, use the ASP.NET Core development certificate rather than changing container behavior:

```powershell
dotnet dev-certs https --trust
$env:ASPNETCORE_URLS = 'https://localhost:7080;http://localhost:5080'
.\start.ps1 -Action run
```

## Azure Container Apps

Azure deployments use [azure.yaml](../azure.yaml) and the Bicep templates under `infra/`. Local deployment context belongs in the git-ignored root `.env`; [.env.example](../.env.example) is the tracked template. Never place a subscription ID or credential in tracked documentation or configuration.

```dotenv
AZURE_SUBSCRIPTION_ID=<subscription-guid>
AZURE_LOCATION=eastus2
AZURE_ENV_NAME=mockapi-dev
AZURE_CUSTOM_DOMAIN=
AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=CNAME
AZURE_DASHBOARD_USERNAME=
AZURE_DASHBOARD_PASSWORD=
```

Set both dashboard values to enable Basic authentication, or leave both empty to deploy the dashboard and management API without authentication. The password must contain at least 8 characters. `start.ps1` derives the PBKDF2-SHA256 password hash locally and sends only the username and hash to azd; plaintext is confined to the ignored `.env` or process environment. Partial credentials fail preflight.

Run `./start.ps1 -Action azure-setup -InstallMissing` to install or verify Azure Developer CLI 1.27.1 or newer and Azure CLI through WinGet. This action does not authenticate or configure an environment. Then run `./start.ps1 -Action azure-check`; it verifies the cached login status, starts interactive login when needed, synchronizes the named azd environment, parses the Docker service, and builds Bicep locally. The local `check` and `setup` actions remain independent of Azure tooling.

Use `pathway-azure-initial` for the normal first deployment and `pathway-azure-update` for subsequent application updates. Interactive menu choices `p1` and `p2` select those pathways. Both stop on the first failure: they run the test suite and managed build before delegating to `azure-up` or `azure-deploy`, respectively. The equivalent Bash actions use `./start.sh --action pathway-azure-initial` and `./start.sh --action pathway-azure-update`.

`azure-up` builds the native container through WSLC, runs `azd provision --preview`, and then uses normal PowerShell and azd confirmation behavior before creating resources. Interactive menu action `a4` runs `azure-deploy`, which builds and pushes the image, updates the Container App to pull it, verifies `/health/ready`, and prints the HTTPS endpoint. Menu action `a5` runs the registry-only `azure-push` path; it uses `azd publish` without changing the running Container App. Menu action `a6` runs `azure-import`; it imports the public `docker.io/simonkurtzmsft/mockapi:v<Version>` image into ACR as `mockapi:v<Version>` without Docker Hub credentials, updates the existing Container App, verifies readiness, and prints the endpoint. Azure image publication uses an ACR remote build and does not require a local Docker CLI. `azure-down` confirms resource-group deletion, submits the request with Azure CLI `--no-wait`, and returns while Azure completes deletion asynchronously. Use `-WhatIf` to inspect a PowerShell-managed Azure mutation without running it.

`azure-import` requires the Azure environment to be provisioned first. It performs an explicit import for the current application version and does not configure ACR Artifact Cache or automatically synchronize later Docker Hub tags. Published version tags are immutable and must not be moved or overwritten.

The deployment uses these controls:

- Container Apps Consumption at `0.25` vCPU and `0.5 GiB,` with zero minimum and one maximum replica.
- A user-assigned managed identity with `AcrPull` on the Basic registry and `Storage Blob Data Contributor` scoped to the configuration container; registry administration and storage local authentication are disabled.
- HTTPS-only external ingress and HTTP liveness/readiness probes.
- A private Standard LRS Blob container for saved configuration, accessed with managed identity from a VNet-integrated Container Apps environment.
- Dashboard and management API enabled, with optional Basic authentication controlled by the paired dashboard values in `.env`; OpenAPI and Swagger UI remain disabled.
- Log Analytics with 30-day retention, required by the managed environment configuration and linked from the deployed dashboard footer.

Container Apps Express is not currently compatible with MockAPI's deployment requirements. Track its feature readiness in [FEATURES.md](FEATURES.md#azure-container-apps-express).

Container compute scales to zero. Basic ACR, Blob storage, private endpoints, private DNS, and Log Analytics ingestion can still incur charges until `azure-down` removes the resource group. The storage account disables shared-key authentication and public network access; the application reaches its Blob private endpoint through the dedicated environment subnet.

### Custom domain and Azure-managed TLS

Set `AZURE_CUSTOM_DOMAIN=api.example.com` in your ignored environment file to request a free [Azure Container Apps managed certificate](https://learn.microsoft.com/azure/container-apps/custom-domains-managed-certificates). Use a hostname only, not a URL, wildcard, IP address, or hostname with a trailing dot. Internationalized domains must use their ASCII/Punycode form. Nonempty process-environment values override file values, as with the other Azure settings.

Use `AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=CNAME` (the default) for a subdomain, or `HTTP` for an apex domain. Apex status cannot reliably be inferred from the number of labels, so select the method explicitly.

The `azure-up`, `azure-deploy`, and `azure-import` actions first verify the generated Azure endpoint, then ask Azure to add the hostname, issue and bind the Azure-managed certificate, and verify `/health/ready` over HTTPS at the custom hostname with normal certificate validation. The final URL is the custom domain only after that check succeeds. `azure-push` remains registry-only and does not change DNS, certificates, or domain bindings.

After azd reports deployment success, the CLI announces readiness verification before looking up the endpoint. It prints the target URL and attempt number for each readiness check, explains five-second retry waits, and announces custom-domain configuration before querying Azure. These messages distinguish cold-start and domain-setup waits from an idle command.

Successful Azure actions finish with an `All done!` summary and a labeled `Dashboard URL`, so the final link is clearly identified as the dashboard entry point. When custom-domain DNS is still pending, the link remains the generated Azure URL.

DNS is managed by you, not by this repository. The CLI prints four numbered steps with indented record rows, aligned DNS arrows, blank lines separating headings and record values from explanations, and retry commands that retain your selected environment-file path. Required actions are labeled `ACTION REQUIRED` and shown in yellow; exact DNS entries and retry commands are shown in cyan. The CAA guidance is labeled `OPTIONAL CHECK` because it applies only when the domain already restricts certificate issuers:

1. **Set up the CNAME record** at your configured subdomain (for example, `api.example.com`), pointing directly to the generated Container App hostname printed by the CLI. Do not use a proxy or intermediate CNAME. For an apex domain with `HTTP` validation, create an **A record** pointing to the printed Container Apps environment static IP instead. **Why:** this routes traffic to the app and enables domain validation.
2. **Set up the TXT record** at `asuid.<configured-hostname>`, using the Container App domain-verification ID printed by the CLI. **Why:** Azure needs proof that you control the hostname before it accepts the custom domain. Keep this record to protect against dangling-domain takeover.
3. **Set up a CAA record if your domain restricts certificate issuers.** At the applicable DNS zone, allow `0 issue "digicert.com"`: flags `0`, tag `issue`, value `digicert.com`. Do not place a CAA record at a name that already has a CNAME; use the applicable parent zone instead. Preserve other required issuer entries. If no CAA policy restricts issuers, no CAA change is required. **Why:** DigiCert must be authorized to issue and renew the free Azure-managed certificate.
4. **Wait for DNS propagation, then press `4` at the interactive continuation prompt.** **Why:** this retries only custom-domain validation, hostname creation, managed-certificate issuance/binding, and HTTPS readiness. It does not rebuild or redeploy the application. If DNS is still pending, the prompt appears again. Any other key returns to the current menu without clearing the DNS instructions or requiring another keypress. Successful continuation also preserves the output when returning to the menu.

   Explicit command-line actions remain non-interactive. To retry later, rerun the deployment command from the repository root. Use either command below for the default environment file; for another file, use the exact command printed by the CLI. Keep the same process-environment overrides, if any. This fallback redeploys the application before completing domain setup.

   ```powershell
   .\start.ps1 -Action azure-deploy -EnvironmentFile '.env'
   ```

   ```bash
   ./start.sh --action azure-deploy --environment-file '.env'
   ```

DNS provider editors often expect names relative to the zone: use `@` and `asuid` for an apex, or `api` and `asuid.api` for an `api` subdomain. The CLI prints fully qualified names.

Azure does not accept an unbound hostname before its ownership and routing records validate. Missing or propagating DNS is an **expected pending state**, not a deployment error. If Azure reports `InvalidCustomHostNameValidation`, the CLI displays a pending-DNS message instead of the raw `ERROR` diagnostic and keeps the generated endpoint as the successful deployment result. The requested hostname remains in your ignored environment configuration; it is not usable over custom-domain HTTPS until validation and certificate binding succeed. Other failures, such as authorization or certificate-issuance errors, still fail the command and display their diagnostics.

Certificate issuance can take several minutes. Azure renews the certificate automatically while the DNS records remain correct and the app remains running and publicly accessible to DigiCert validation traffic. Do not put a DNS proxy or access restriction in front of validation. Keep the ownership TXT record to protect against dangling-domain takeover.

These operations require Azure CLI authentication (`az login`) as well as azd authentication, and permission to read the selected subscription's resources and update the app and environment certificates. Domain commands explicitly target the configured subscription rather than relying on the Azure CLI's current subscription.

Use the developer CLI for provisioning: before `azure-up` previews or provisions infrastructure, it reads and preserves all existing ingress domain bindings in the internal azd value `AZURE_CUSTOM_DOMAINS`. Do not add this internal value to your root environment file or edit it manually. Direct `azd up` bypasses this refresh and the post-deployment certificate workflow. Existing managed bindings are reused; non-managed certificate bindings are not silently replaced. A failed state read stops provisioning.

The infrastructure receives this snapshot through a typed `customDomains` array parameter. This lets azd parse the JSON bindings without inserting their nested quotation marks into a serialized string parameter, which can otherwise cause `error unmarshalling Bicep template parameters` during preview. An unset snapshot defaults to an empty array.

Leaving `AZURE_CUSTOM_DOMAIN` empty disables domain automation and retains the generated Azure URL as the reported endpoint. Clearing or changing the setting does **not** delete old bindings or certificates. Remove obsolete bindings explicitly in Azure and update your DNS provider; unrelated bindings are preserved.

### Azure Files migration

An environment created by an earlier template without a customer virtual network must be recreated before this architecture is deployed. Do not apply the preview that adds `vnetConfiguration` to that existing environment. After explicit approval, run `azure-down`, confirm the resource group is removed, and then run `azure-up` to create the VNet-integrated environment and Blob resources. This teardown also removes the obsolete Azure Files share and environment-storage resource that an incremental deployment would otherwise retain.

This migration discards any configuration that existed only in the old file share. Export a valid configuration first when the old deployment is reachable. The current failed deployment cannot mount its share under enforced storage policy, so there is no application-readable persisted state to migrate.

## Release Evidence

Pull requests build, run, generate an SBOM for, and scan `linux/amd64` and `linux/arm64` images without registry credentials. Release/manual automation verifies native architecture builds, emits a multi-platform OCI artifact, and then publishes `simonkurtzmsft/mockapi:v<Version>` to Docker Hub. Published GitHub releases use their release tag; manual runs require the same tag through the `tagname` input. The tag must exactly match the application `<Version>` in `v<Version>` form. Configure the `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets before publishing.
