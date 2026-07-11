# Operations and Security

## Health and Exposure

- Liveness: `/health/live`
- Readiness: `/health/ready`
- Dashboard: `/`
- Management API: `/__mockapi/api`
- Swagger UI: `/__mockapi/swagger`

The dashboard and management API are unauthenticated. Bind to loopback, place them behind a protected ingress, or disable them independently with `MockApi__EnableDashboard=false` and `MockApi__EnableManagementApi=false`.

MockAPI suppresses the Kestrel `Server` header. It does not log configured request or response bodies, authorization headers, cookies, or query values. Statistics are bounded, process-local, and reset on restart.

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

## Release Evidence

Pull requests build, run, generate an SBOM for, and scan `linux/amd64` and `linux/arm64` images without registry credentials. Release/manual automation verifies native architecture builds and emits a multi-platform OCI artifact. Docker Hub publishing remains disabled until its namespace, credentials, and tag policy are explicitly approved.
