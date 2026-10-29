# Feature Readiness

## Installable dashboard

The MockAPI dashboard is a progressive web app. Open your instance over HTTPS (or loopback HTTP
for development) and use your browser's **Install app** command. On iPhone or iPad, use Safari's
**Share > Add to Home Screen**. Availability and wording depend on the browser and operating system.
Installation opens the same instance in a standalone window; existing administrator authentication
still applies. Each origin identifies a separate installation.
The header displays the current instance's hostname, without protocol, port, or path, in both
browser tabs and installed app windows, including on mobile.

The dashboard requires a live connection. After the first successful visit activates its service
worker, an offline launch shows a generic **Connection unavailable** page with a **Reconnect to
MockAPI** link. It stores no dashboard pages, endpoint definitions, statistics, credentials, or
request/response content in service-worker caches. Offline changes are not supported or queued.
Mock requests, management API calls, event streams, and health probes are never intercepted.
Live server errors and authentication challenges remain unchanged.

The service worker updates through the browser's normal lifecycle; close existing dashboard windows
and reopen after a deployment to activate a waiting update. Browsers without service-worker support,
and non-loopback HTTP deployments, retain the ordinary live dashboard without offline support.
Disabling the dashboard also disables its manifest, icons, and service-worker routes; an already
installed app is not remotely uninstalled.

## Azure Container Apps Express

This matrix tracks the Azure Container Apps features MockAPI currently needs and their availability in the existing Consumption deployment versus Container Apps Express. Express is in preview, and its status is a documentation snapshot as of July 14, 2026. **Available** means the required capability is supported, **Partial** means it is not fully supported, and **In development** means MockAPI must not depend on it in an Express deployment yet.

| MockAPI requirement             | Why MockAPI needs it                                                                   | ACA Consumption        | ACA Express (preview)                                                                  |
| ------------------------------- | -------------------------------------------------------------------------------------- | ---------------------- | -------------------------------------------------------------------------------------- |
| Consumption CPU                 | Run the HTTP application with pay-as-you-go compute                                    | Available              | Available                                                                              |
| Minimum replica floor           | Keep one replica continuously available for predictable latency                        | Available              | Available                                                                              |
| Container image deployment      | Deploy the published MockAPI image                                                     | Available              | Available for anonymous and token-based pulls                                          |
| Managed identity for image pull | Pull from ACR while registry administration is disabled                                | Available              | In development                                                                         |
| Environment variables           | Supply non-secret application settings and Blob URI                                    | Available              | Available                                                                              |
| Secrets                         | Supply the optional dashboard password hash without exposing it as a plain setting     | Available              | In development                                                                         |
| HTTPS external ingress          | Expose mock routes, health endpoints, and optionally protected administration surfaces | Available              | Available                                                                              |
| Health probes                   | Gate liveness, readiness, and deployment verification                                  | Available              | In development                                                                         |
| Log Analytics logs              | Retain platform and application logs for operations                                    | Available              | Available                                                                              |
| Managed identity at runtime     | Authenticate to Blob storage without shared keys                                       | Available              | In development                                                                         |
| VNet integration                | Reach the private Blob endpoint and its private DNS zone                               | Available              | In development                                                                         |
| HTTP autoscaling                | Keep the current request-based scale rule and replica bounds                           | Available              | KEDA-based autoscale in development; scale to zero and multiple replicas are available |
| Rolling updates                 | Replace the running image without an avoidable outage                                  | Available              | Partial                                                                                |
| Single-revision management      | Preserve the current one-active-revision deployment model                              | Available              | In development                                                                         |
| CORS policy                     | Preserve the current ingress CORS configuration for cross-origin mock clients          | Available              | In development                                                                         |
| Deployment region               | Keep the default `eastus2` deployment unless explicitly changed                        | Available in `eastus2` | Not available in `eastus2`; preview is limited to West Central US and East Asia        |

Express feature reference: [Azure Container Apps express overview - Supported features](https://learn.microsoft.com/en-us/azure/container-apps/express-overview#supported-features)

Container Apps Express is not currently compatible with MockAPI's deployment requirements. Reassess it when managed identity for image pull and app runtime, secrets, health probes, VNet integration, single-revision management, and a suitable deployment region are supported. Blob persistence must remain private and identity-based; do not weaken storage networking or authentication solely to adopt Express.
