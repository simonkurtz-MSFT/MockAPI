---
description: "Use when changing Bicep templates, parameters, Azure resource definitions, or azd infrastructure outputs."
applyTo: "**/*.bicep,**/bicepconfig.json"
---

# Bicep Guidelines

- Preserve the `azd` contract in `azure.yaml`, `infra/main.bicep`, and `infra/main.parameters.json`: subscription scope, environment and location parameters, resource-group and service tags, and required outputs must remain synchronized.
- Use modern Bicep syntax, symbolic resource references, implicit dependencies, and resource accessor functions. Add an explicit `dependsOn` only for a real dependency that Bicep cannot infer.
- Organize templates in this order when applicable: Parameters, Variables, Resources, Outputs. Use the existing section-header format and camelCase symbolic names.
- Add `@description` decorators to parameters and variables. Apply validation decorators when Azure imposes a stable, meaningful constraint.
- Keep resource names deterministic and globally unique where required. Lead with the Microsoft Cloud Adoption Framework [resource abbreviation](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations), followed by the workload/environment, region, and deterministic token when the service permits them. Compact or omit separators only for Azure naming constraints, preserve the token when truncating, do not add a generic `az` prefix, and never include credentials or user-specific values.
- Add the relevant `https://learn.microsoft.com/azure/templates/...` reference immediately above each Azure resource declaration.
- Prefer managed identities and least-privilege role assignments. Never hard-code credentials, emit secrets, enable registry administration, or place secret values in outputs.
- Keep storage shared-key access disabled. Use the user-assigned identity with a container-scoped data-plane role for Azure persistence, and use private endpoints plus private DNS when policy disables public storage access.
- Keep APIs on supported stable versions. Verify a version change against the Azure template reference and deployment compatibility before adopting it.
- Preserve deploy-time parallelism. References should express only real dependencies; do not serialize unrelated resources.
- Preserve existing ingress domain bindings through the developer CLI's `AZURE_CUSTOM_DOMAINS` snapshot before reprovisioning. Bind new custom hostnames with Azure-managed certificates after deployment and user-managed DNS validation; never remove existing bindings merely because the optional domain setting is empty.
- Run `./start.ps1 -Action azure-check` after changes when authenticated Azure context is available. At minimum, run `az bicep build --file infra/main.bicep` and require zero diagnostics.
