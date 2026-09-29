# Feature Readiness

## Azure Container Apps Express

This matrix tracks the Azure Container Apps features MockAPI currently needs and their availability in the existing Consumption deployment versus Container Apps Express. Express is in preview, and its status is a documentation snapshot as of July 14, 2026. **Available** means the required capability is supported, **Partial** means it is not fully supported, and **In development** means MockAPI must not depend on it in an Express deployment yet.

| MockAPI requirement             | Why MockAPI needs it                                                                   | ACA Consumption        | ACA Express (preview)                                                                  |
| ------------------------------- | -------------------------------------------------------------------------------------- | ---------------------- | -------------------------------------------------------------------------------------- |
| Consumption CPU                 | Run the HTTP application with pay-as-you-go compute                                    | Available              | Available                                                                              |
| Scale to zero                   | Avoid idle container compute charges                                                   | Available              | Available                                                                              |
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
