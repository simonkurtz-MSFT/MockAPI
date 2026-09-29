#!/usr/bin/env pwsh
<#
.SYNOPSIS
    MockAPI Developer CLI.

.DESCRIPTION
    Checks and prepares the local toolchain, restores packages, builds and tests
    the solution, collects coverage, publishes both Linux runtime targets, and
    manages the native development container through WSLC or Docker, and validates or
    deploys the application to Azure Container Apps through azd.

.EXAMPLE
    .\start.ps1
    .\start.ps1 -Action setup
    .\start.ps1 -Action setup -InstallMissing
    .\start.ps1 -Action run
    .\start.ps1 -Action coverage
    .\start.ps1 -Action container-build
    .\start.ps1 -Action container-run
    .\start.ps1 -Action azure-setup -InstallMissing
    .\start.ps1 -Action azure-check
    .\start.ps1 -Action azure-import
    .\start.ps1 -Action azure-push
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [ValidateSet(
    'menu', 'help', 'check', 'setup', 'dependencies-update', 'pnpm-update', 'restore', 'format', 'lint', 'build', 'run', 'run-tutorial', 'test', 'coverage',
    'publish', 'validate', 'container-engine-wslc', 'container-engine-docker', 'container-build', 'container-run', 'container-test',
    'container-showcase', 'container-logs', 'container-status', 'container-stop',
    'container-remove', 'azure-setup', 'azure-check', 'azure-up', 'azure-import', 'azure-push', 'azure-deploy', 'azure-down',
    'pathway-azure-initial', 'pathway-azure-update', 'all')]
  [string] $Action = 'menu',

  [switch] $InstallMissing,

  [switch] $SkipContainerCheck,

  [ValidateSet('wslc', 'docker')]
  [string] $ContainerEngine = 'wslc',

  [ValidateNotNullOrEmpty()]
  [string] $Configuration = 'Release',

  [ValidateNotNullOrEmpty()]
  [string] $ImageName = 'mockapi:dev',

  [ValidateNotNullOrEmpty()]
  [string] $ContainerName = 'mockapi-dev',

  [ValidateNotNullOrEmpty()]
  [string] $VolumeName = 'mockapi-data',

  [string] $NuGetSource = '',

  [ValidateRange(1, 65535)]
  [int] $Port = 8080,

  [ValidateNotNullOrEmpty()]
  [string] $EnvironmentFile = '.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$originalConsoleOutputEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$repositoryRoot = $PSScriptRoot
$solutionFile = Join-Path $repositoryRoot 'MockAPI.slnx'
$projectFile = Join-Path $repositoryRoot 'src/MockAPI/MockAPI.csproj'
$dockerfile = Join-Path $repositoryRoot 'Dockerfile'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$coverageDirectory = Join-Path $artifactsDirectory 'coverage'
$publishDirectory = Join-Path $artifactsDirectory 'publish'
$fieldWidth = 20
$script:DomainContinuationPrompted = $false
$containerImageIdLabel = 'mockapi.image-id'
$requiredAzureEnvironmentKeys = @(
  'AZURE_SUBSCRIPTION_ID',
  'AZURE_LOCATION',
  'AZURE_ENV_NAME'
)
$optionalAzureEnvironmentKeys = @(
  'AZURE_CUSTOM_DOMAIN',
  'AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD',
  'AZURE_DASHBOARD_USERNAME',
  'AZURE_DASHBOARD_PASSWORD'
)
$azureEnvironmentKeys = @($requiredAzureEnvironmentKeys) + @($optionalAzureEnvironmentKeys)

function Get-AzureDeploymentConfiguration {
  $environmentPath = if ([IO.Path]::IsPathRooted($EnvironmentFile)) {
    $EnvironmentFile
  }
  else {
    Join-Path $repositoryRoot $EnvironmentFile
  }
  if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
    throw "Azure settings were not found at '$environmentPath'. Copy '.env.example' to '.env' and set AZURE_SUBSCRIPTION_ID."
  }

  $fileValues = @{}
  $lineNumber = 0
  foreach ($line in Get-Content -LiteralPath $environmentPath) {
    $lineNumber++
    $trimmedLine = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmedLine) -or $trimmedLine.StartsWith('#')) {
      continue
    }
    if ($trimmedLine -notmatch '^(?<key>[A-Z][A-Z0-9_]*)=(?<value>.*)$') {
      throw "Malformed environment entry at ${environmentPath}:$lineNumber. Expected KEY=value."
    }

    $key = $Matches.key
    if ($key -notin $azureEnvironmentKeys) {
      throw "Unknown Azure deployment key '$key' at ${environmentPath}:$lineNumber."
    }
    if ($fileValues.ContainsKey($key)) {
      throw "Duplicate Azure deployment key '$key' at ${environmentPath}:$lineNumber."
    }

    $value = $Matches.value.Trim()
    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
        ($value.StartsWith("'") -and $value.EndsWith("'"))) {
      if ($value.Length -lt 2) {
        throw "Malformed quoted value for '$key' at ${environmentPath}:$lineNumber."
      }
      $value = $value.Substring(1, $value.Length - 2)
    }
    elseif ($value.Contains('"') -or $value.Contains("'")) {
      throw "Malformed quoted value for '$key' at ${environmentPath}:$lineNumber."
    }
    $fileValues[$key] = $value
  }

  $configuration = @{}
  foreach ($key in $azureEnvironmentKeys) {
    $processValue = [Environment]::GetEnvironmentVariable($key, 'Process')
    $value = if (-not [string]::IsNullOrWhiteSpace($processValue)) {
      $processValue.Trim()
    }
    elseif ($fileValues.ContainsKey($key)) {
      ([string] $fileValues[$key]).Trim()
    }
    else {
      $null
    }
    if ([string]::IsNullOrWhiteSpace($value) -and $key -in $requiredAzureEnvironmentKeys) {
      throw "Azure deployment setting '$key' is required. Set it in the process environment or '$environmentPath'."
    }
    $configuration[$key] = if ($null -eq $value) { '' } else { $value }
  }

  if ($configuration.AZURE_SUBSCRIPTION_ID -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
    throw "Azure deployment setting 'AZURE_SUBSCRIPTION_ID' must be a GUID."
  }
  if ($configuration.AZURE_LOCATION -notmatch '^[a-z0-9]+$') {
    throw "Azure deployment setting 'AZURE_LOCATION' must use an Azure CLI location name such as 'eastus2'."
  }
  if ($configuration.AZURE_ENV_NAME -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$') {
    throw "Azure deployment setting 'AZURE_ENV_NAME' must contain 1-64 letters, numbers, or hyphens and cannot start with a hyphen."
  }
  $hasDashboardUsername = -not [string]::IsNullOrWhiteSpace($configuration.AZURE_DASHBOARD_USERNAME)
  $domain = $configuration.AZURE_CUSTOM_DOMAIN.ToLowerInvariant()
  if ($domain -and ($domain.Length -gt 253 -or $domain -notmatch '^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$')) {
    throw "Azure deployment setting 'AZURE_CUSTOM_DOMAIN' must be a DNS hostname, without a scheme, path, port, wildcard, or trailing dot."
  }
  $configuration.AZURE_CUSTOM_DOMAIN = $domain
  if (-not $configuration.AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD) {
    $configuration.AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD = 'CNAME'
  }
  if ($configuration.AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD -cnotin @('CNAME', 'HTTP')) {
    throw "Azure deployment setting 'AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD' must be CNAME (subdomain) or HTTP (apex domain)."
  }
  $hasDashboardPassword = -not [string]::IsNullOrWhiteSpace($configuration.AZURE_DASHBOARD_PASSWORD)
  if ($hasDashboardUsername -ne $hasDashboardPassword) {
    throw "Azure deployment settings 'AZURE_DASHBOARD_USERNAME' and 'AZURE_DASHBOARD_PASSWORD' must both be set or both be empty."
  }
  if ($hasDashboardUsername -and $configuration.AZURE_DASHBOARD_USERNAME.Contains(':')) {
    throw "Azure deployment setting 'AZURE_DASHBOARD_USERNAME' cannot contain a colon."
  }
  if ($hasDashboardPassword -and $configuration.AZURE_DASHBOARD_PASSWORD.Length -lt 8) {
    throw "Azure deployment setting 'AZURE_DASHBOARD_PASSWORD' must contain at least 8 characters."
  }

  return $configuration
}

function Invoke-AzureCheck {
  $configuration = Get-AzureDeploymentConfiguration
  Assert-Azd
  Assert-AzureAuthentication
  Set-AzdEnvironment -Configuration $configuration
  Invoke-Tool -Executable 'azd' -Operation 'Validating Azure Developer CLI project' -Arguments @(
    'show', '--environment', $configuration.AZURE_ENV_NAME, '--no-prompt'
  )
  Assert-AzureInfrastructure
  Write-Field 'Azure environment' $configuration.AZURE_ENV_NAME
  Write-Field 'Azure subscription' $configuration.AZURE_SUBSCRIPTION_ID
  Write-Field 'Azure location' $configuration.AZURE_LOCATION
  Write-Host ''
  Write-Host 'Azure deployment preflight passed.' -ForegroundColor Green
  return $configuration
}

function Assert-Azd {
  if (-not (Get-Command azd -ErrorAction SilentlyContinue)) {
    if (-not $InstallMissing) {
      throw "Azure Developer CLI is missing. Run '.\start.ps1 -Action azure-setup -InstallMissing'."
    }
    Install-Azd
    Update-ProcessPath
  }
  if (-not (Get-Command azd -ErrorAction SilentlyContinue)) {
    throw 'Azure Developer CLI is still unavailable after installation. Restart the terminal and run Azure tooling setup again.'
  }

  $minimumVersion = [Version] '1.27.1'
  $versionResult = Invoke-NativeTool -Executable 'azd' -Arguments @('version')
  $versionText = [string] ($versionResult.Output -join ' ')
  if ($versionResult.ExitCode -ne 0 -or $versionText -notmatch 'azd version (?<version>\d+\.\d+\.\d+)') {
    throw 'Unable to resolve the Azure Developer CLI version.'
  }
  $resolvedVersion = [Version] $Matches.version
  if ($resolvedVersion -lt $minimumVersion -and $InstallMissing) {
    Install-Azd
    Update-ProcessPath
    $versionResult = Invoke-NativeTool -Executable 'azd' -Arguments @('version')
    $versionText = [string] ($versionResult.Output -join ' ')
    if ($versionResult.ExitCode -ne 0 -or $versionText -notmatch 'azd version (?<version>\d+\.\d+\.\d+)') {
      throw 'Unable to resolve the Azure Developer CLI version after installation.'
    }
    $resolvedVersion = [Version] $Matches.version
  }
  if ($resolvedVersion -lt $minimumVersion) {
    throw "Azure Developer CLI $minimumVersion or newer is required, but $resolvedVersion is installed."
  }
  Write-Field 'Azure Dev CLI' ([string] $resolvedVersion) Green
}

function Assert-AzureCli {
  if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    if (-not $InstallMissing) {
      throw "Azure CLI is missing. Run '.\start.ps1 -Action azure-setup -InstallMissing'."
    }
    Install-AzureCli
    Update-ProcessPath
  }
  if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is still unavailable after installation. Restart the terminal and run Azure tooling setup again.'
  }

  $versionResult = Invoke-NativeTool -Executable 'az' -Arguments @('version', '--output', 'json')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Unable to resolve the Azure CLI version.'
  }
  try {
    $versionDocument = ([string] ($versionResult.Output -join "`n")) | ConvertFrom-Json
    $resolvedVersion = [string] $versionDocument.'azure-cli'
  }
  catch {
    throw 'Unable to parse the Azure CLI version.'
  }
  if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
    throw 'Unable to resolve the Azure CLI version.'
  }
  Write-Field 'Azure CLI' $resolvedVersion Green
}

function Invoke-AzureSetup {
  Assert-Azd
  Assert-AzureCli
  Write-Host ''
  Write-Host 'Azure tooling is ready.' -ForegroundColor Green
}

function Assert-AzureAuthentication {
  $statusArguments = @('auth', 'login', '--check-status', '--no-prompt')
  $statusResult = Invoke-NativeTool -Executable 'azd' -Arguments $statusArguments
  if ($statusResult.ExitCode -eq 0) {
    Write-Field 'Azure authentication' 'Authenticated' Green
    return
  }

  Write-Host 'Azure authentication is required. Starting interactive azd login.' -ForegroundColor Yellow
  Invoke-Tool -Executable 'azd' -Operation 'Authenticating Azure Developer CLI' -Arguments @('auth', 'login')
  $statusResult = Invoke-NativeTool -Executable 'azd' -Arguments $statusArguments
  if ($statusResult.ExitCode -ne 0) {
    throw 'Azure Developer CLI authentication could not be verified after login.'
  }
  Write-Field 'Azure authentication' 'Authenticated' Green
}

function Set-AzdEnvironment {
  param([Parameter(Mandatory)][hashtable] $Configuration)

  $environmentName = [string] $Configuration.AZURE_ENV_NAME
  $selectResult = Invoke-NativeTool -Executable 'azd' -Arguments @(
    'env', 'select', $environmentName, '--no-prompt'
  )
  if ($selectResult.ExitCode -ne 0) {
    Invoke-Tool -Executable 'azd' -Operation "Creating azd environment $environmentName" -Arguments @(
      'env', 'new', $environmentName,
      '--subscription', $Configuration.AZURE_SUBSCRIPTION_ID,
      '--location', $Configuration.AZURE_LOCATION,
      '--no-prompt'
    )
  }

  foreach ($key in $requiredAzureEnvironmentKeys) {
    Invoke-Tool -Executable 'azd' -Operation "Setting azd environment value $key" -Arguments @(
      'env', 'set', $key, ([string] $Configuration[$key]),
      '--environment', $environmentName, '--no-prompt'
    )
  }

  $dashboardUsername = [string] $Configuration.AZURE_DASHBOARD_USERNAME
  $dashboardPasswordHash = ''
  if (-not [string]::IsNullOrWhiteSpace($dashboardUsername)) {
    $securePassword = ConvertTo-SecureString ([string] $Configuration.AZURE_DASHBOARD_PASSWORD) -AsPlainText -Force
    try {
      $dashboardPasswordHash = New-DashboardPasswordHash -Password $securePassword
    }
    finally {
      $securePassword.Dispose()
    }
  }
  foreach ($entry in @(
      @{ Key = 'AZURE_DASHBOARD_USERNAME'; Value = $dashboardUsername },
      @{ Key = 'AZURE_DASHBOARD_PASSWORD_HASH'; Value = $dashboardPasswordHash }
    )) {
    if ([string]::IsNullOrWhiteSpace($entry.Value)) {
      Invoke-Tool -Executable 'azd' -Operation "Clearing azd environment value $($entry.Key)" -Arguments @(
        'env', 'set', "$($entry.Key)=",
        '--environment', $environmentName, '--no-prompt'
      )
    }
    else {
      Invoke-Tool -Executable 'azd' -Operation "Setting azd environment value $($entry.Key)" -Arguments @(
        'env', 'set', $entry.Key, $entry.Value,
        '--environment', $environmentName, '--no-prompt'
      )
    }
  }
}

function Assert-AzureInfrastructure {
  Assert-AzureCli
  $azureArtifactsDirectory = Join-Path $artifactsDirectory 'azure'
  New-Item -ItemType Directory -Path $azureArtifactsDirectory -Force | Out-Null
  Invoke-Tool -Executable 'az' -Operation 'Building Azure Bicep infrastructure' -Arguments @(
    'bicep', 'build', '--file', (Join-Path $repositoryRoot 'infra/main.bicep'),
    '--outfile', (Join-Path $azureArtifactsDirectory 'main.json')
  )
}

function Get-AzureServiceUri {
  param([Parameter(Mandatory)][string] $EnvironmentName)

  $serviceUri = Get-AzdEnvironmentValue -EnvironmentName $EnvironmentName -Name 'SERVICE_MOCKAPI_URI'
  if (-not [Uri]::IsWellFormedUriString($serviceUri, [UriKind]::Absolute)) {
    throw 'Azure deployment returned an invalid SERVICE_MOCKAPI_URI value.'
  }
  return $serviceUri.TrimEnd('/')
}

function Get-AzdEnvironmentValue {
  param(
    [Parameter(Mandatory)][string] $EnvironmentName,
    [Parameter(Mandatory)][string] $Name
  )

  $valueResult = Invoke-NativeTool -Executable 'azd' -Arguments @(
    'env', 'get-value', $Name, '--environment', $EnvironmentName, '--no-prompt'
  )
  if ($valueResult.ExitCode -ne 0) {
    throw "Azure environment value '$Name' was not available in azd environment '$EnvironmentName'."
  }
  $value = ([string] ($valueResult.Output | Select-Object -First 1)).Trim().Trim('"')
  if ([string]::IsNullOrWhiteSpace($value)) {
    throw "Azure environment value '$Name' was empty in azd environment '$EnvironmentName'."
  }
  return $value
}

function Test-AzureDeployment {
  param(
    [Parameter(Mandatory)][string] $EnvironmentName,
    [string] $ServiceUri
  )

  Write-Host ''
  Write-Host 'Next: verify Azure deployment readiness. Waiting for the app to start may take a few minutes.' -ForegroundColor Cyan
  if (-not $ServiceUri) {
    $ServiceUri = Get-AzureServiceUri -EnvironmentName $EnvironmentName
  }
  $readinessUri = "$serviceUri/health/ready"
  $response = $null
  for ($attempt = 1; $attempt -le 6; $attempt++) {
    Write-Host "Readiness check ($attempt/6): $readinessUri" -ForegroundColor Cyan
    try {
      $candidate = Invoke-WebRequest -Uri $readinessUri -TimeoutSec 30 -SkipHttpErrorCheck
      if ($candidate.StatusCode -eq 200 -and $candidate.Content -match '"status"\s*:\s*"ready"') {
        $response = $candidate
        break
      }
    }
    catch {
      if ($attempt -eq 6) {
        break
      }
    }

    if ($attempt -lt 6) {
      Write-Host 'Not ready yet; retrying in 5 seconds.' -ForegroundColor Yellow
      Start-Sleep -Seconds 5
    }
  }

  if ($null -eq $response) {
    throw "Azure smoke test expected readiness at '$readinessUri' to return HTTP 200 with ready status."
  }
  Write-Field 'Azure smoke test' "$readinessUri -> HTTP $($response.StatusCode)" Green
  return $serviceUri
}

function Write-AzureServiceSummary {
  param([Parameter(Mandatory)][string] $ServiceUri)

  $displayUri = "$($ServiceUri.TrimEnd('/'))/"
  Write-Host ''
  Write-Host 'All done! The Azure action completed successfully.' -ForegroundColor Green
  Write-Host ''
  Write-Host 'Dashboard URL:'
  Write-Host $displayUri -ForegroundColor Green
}

function Get-AzureCliValue {
  param([Parameter(Mandatory)][string[]] $Arguments)

  $result = Invoke-NativeTool -Executable 'az' -Arguments $Arguments
  if ($result.ExitCode -ne 0) {
    throw "Azure CLI failed while reading custom-domain deployment state: $($result.Output -join [Environment]::NewLine)"
  }
  return ($result.Output -join [Environment]::NewLine).Trim()
}

function Get-AzureContainerAppId {
  param([Parameter(Mandatory)][hashtable] $Configuration)

  $resourceGroup = "rg-$($Configuration.AZURE_ENV_NAME)-$($Configuration.AZURE_LOCATION)"
  # A subscription-level list also succeeds before the resource group exists.
  return Get-AzureCliValue -Arguments @(
    'resource', 'list', '--subscription', $Configuration.AZURE_SUBSCRIPTION_ID,
    '--tag', 'azd-service-name=mockapi',
    '--query', "[?resourceGroup=='$resourceGroup' && type=='Microsoft.App/containerApps'].id | [0]",
    '--output', 'tsv'
  )
}

function Save-AzureDomainBindings {
  param([Parameter(Mandatory)][hashtable] $Configuration)

  $appId = Get-AzureContainerAppId -Configuration $Configuration
  $bindings = '[]'
  if ($appId) {
    $value = Get-AzureCliValue -Arguments @(
      'containerapp', 'show', '--ids', $appId,
      '--query', 'properties.configuration.ingress.customDomains', '--output', 'json'
    )
    $parsed = ConvertFrom-Json -InputObject $value -NoEnumerate
    if ($null -ne $parsed) {
      if ($parsed -isnot [Array]) {
        throw 'Azure returned an invalid custom-domain bindings array.'
      }
      $bindings = ConvertTo-Json -InputObject $parsed -Depth 10 -Compress
    }
  }
  Invoke-Tool -Executable 'azd' -Operation 'Preserving existing custom-domain bindings' -Arguments @(
    'env', 'set', 'AZURE_CUSTOM_DOMAINS', $bindings,
    '--environment', $Configuration.AZURE_ENV_NAME, '--no-prompt'
  )
}

function Invoke-AzureCustomDomainCommand {
  param(
    [Parameter(Mandatory)][string] $Operation,
    [Parameter(Mandatory)][string[]] $Arguments
  )

  Write-Host ''
  Write-Host $Operation -ForegroundColor Cyan
  # Classify expected DNS validation failures before displaying Azure CLI diagnostics.
  $result = Invoke-NativeTool -Executable 'az' -Arguments $Arguments -CaptureStandardError
  if ($result.ExitCode -eq 0) {
    foreach ($line in $result.Output) { Write-Host $line }
    return $true
  }

  $output = $result.Output -join [Environment]::NewLine
  if ($output -match '\bInvalidCustomHostNameValidation\b') {
    Write-Field 'Custom domain' 'Pending DNS validation (expected until DNS is configured and propagated). Complete steps 1-4 above; the generated Azure endpoint remains available.' Yellow
    return $false
  }

  foreach ($line in $result.Output) { Write-Host $line }
  throw "$Operation failed with exit code $($result.ExitCode)."
}

function Set-AzureCustomDomain {
  param([Parameter(Mandatory)][hashtable] $Configuration)

  $domain = [string] $Configuration.AZURE_CUSTOM_DOMAIN
  if (-not $domain) {
    return $false
  }
  Write-Host ''
  Write-Host 'Next: check custom-domain configuration and Azure-managed certificate status.' -ForegroundColor Cyan
  $appId = Get-AzureContainerAppId -Configuration $Configuration
  if (-not $appId) {
    throw 'MockAPI Container App was not found. Provision the environment before configuring a custom domain.'
  }
  $app = Get-AzureCliValue -Arguments @('containerapp', 'show', '--ids', $appId, '--output', 'json') |
    ConvertFrom-Json -AsHashtable
  $environmentId = $app.properties['environmentId']
  if (-not $environmentId) {
    $environmentId = $app.properties['managedEnvironmentId']
  }
  $ingress = $app.properties.configuration.ingress
  if (-not $environmentId -or -not $ingress.fqdn -or -not $app.properties.customDomainVerificationId) {
    throw 'Azure returned incomplete Container App domain-validation information.'
  }
  $binding = @($ingress['customDomains'] | Where-Object { $null -ne $_ -and $_.name -eq $domain })
  if ($binding.Count -gt 0 -and $binding[0].bindingType -eq 'SniEnabled') {
    if ($binding[0].certificateId -notmatch '/managedCertificates/') {
      throw 'The configured domain already uses a non-managed certificate. Remove that binding explicitly before switching to an Azure-managed certificate.'
    }
    Write-Field 'Custom domain' "$domain already uses an Azure-managed certificate."
    return $true
  }

  $method = $Configuration.AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD
  $dnsNameWidth = "asuid.$domain".Length
  Write-Host ''
  Write-Host "Custom domain setup: $domain"
  Write-Host 'DNS is managed by you. If these records are already configured, no DNS changes are needed.'
  Write-Host ''
  Write-Host 'Yellow = action required from you. Cyan = exact DNS entry or command to copy.' -ForegroundColor Yellow
  Write-Host ''
  if ($method -eq 'HTTP') {
    $ip = Get-AzureCliValue -Arguments @(
      'containerapp', 'env', 'show', '--ids', $environmentId, '--query', 'properties.staticIp', '--output', 'tsv'
    )
    if (-not $ip) { throw 'Azure did not return the Container Apps environment IP address.' }
    Write-Host '1) ACTION REQUIRED: Set up the A record at your DNS provider.' -ForegroundColor Yellow
    Write-Host ''
    Write-Field '   DNS A' "$($domain.PadRight($dnsNameWidth)) -> $ip" Cyan
    Write-Host ''
    Write-Host '   Why: routes the apex hostname to the Container Apps environment IP for HTTP validation.'
  }
  else {
    Write-Host '1) ACTION REQUIRED: Set up the CNAME record at your DNS provider.' -ForegroundColor Yellow
    Write-Host ''
    Write-Field '   DNS CNAME' "$($domain.PadRight($dnsNameWidth)) -> $($ingress.fqdn)" Cyan
    Write-Host ''
    Write-Host '   Why: routes the subdomain directly to this Container App so Azure can validate it.'
  }
  Write-Host '   Use direct DNS (no proxy or intermediate CNAME).'
  Write-Host ''
  Write-Host '2) ACTION REQUIRED: Set up the TXT record at your DNS provider.' -ForegroundColor Yellow
  Write-Host ''
  Write-Field '   DNS TXT' "asuid.$domain -> $($app.properties.customDomainVerificationId)" Cyan
  Write-Host ''
  Write-Host '   Why: proves that you control the hostname before Azure accepts the custom domain.'
  Write-Host '   Record names above are fully qualified; your provider may require names relative to your DNS zone.'
  Write-Host ''
  Write-Host '3) OPTIONAL CHECK: Update CAA only if your domain restricts certificate issuers.' -ForegroundColor Cyan
  Write-Host ''
  Write-Host '   At the applicable DNS zone, allow: 0 issue "digicert.com" (flags: 0; tag: issue; value: digicert.com).'
  Write-Host '   Do not place CAA at a CNAME name; use the applicable parent zone instead.'
  Write-Host '   Why: authorizes DigiCert to issue and renew the free Azure-managed certificate.'
  Write-Host '   If no CAA policy restricts issuers, no CAA change is required. Preserve other required issuer entries.'
  Write-Host ''
  if ($Action -eq 'menu') {
    Write-Host '4) ACTION REQUIRED: Wait for DNS propagation, then press 4 at the next prompt to continue.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '   Why: retries only custom-domain setup and HTTPS verification; it does not rebuild or redeploy.'
    Write-Host '   Any other key returns to the menu without clearing these instructions.'
  }
  else {
    Write-Host '4) ACTION REQUIRED: Wait for DNS propagation, then rerun either command from the repository root:' -ForegroundColor Yellow
    Write-Host ''
    $powerShellEnvironmentFile = $EnvironmentFile.Replace("'", "''")
    $bashEnvironmentFile = $EnvironmentFile.Replace("'", "'\''")
    Write-Host "   PowerShell: .\start.ps1 -Action azure-deploy -EnvironmentFile '$powerShellEnvironmentFile'" -ForegroundColor Cyan
    Write-Host "   Bash:       ./start.sh --action azure-deploy --environment-file '$bashEnvironmentFile'" -ForegroundColor Cyan
    Write-Host '   Keep the same process-environment overrides, if any.'
    Write-Host '   Why: redeploys the app, automatically adds the custom hostname, issues and binds the certificate,'
    Write-Host '   then verifies HTTPS readiness. Certificate issuance can take several minutes.'
  }
  Write-Host '   Keep the DNS records and public ingress available for automatic certificate renewal.'
  $resourceGroup = "rg-$($Configuration.AZURE_ENV_NAME)-$($Configuration.AZURE_LOCATION)"
  $arguments = @(
    '--name', $app.name, '--resource-group', $resourceGroup,
    '--subscription', $Configuration.AZURE_SUBSCRIPTION_ID, '--hostname', $domain
  )
  if ($binding.Count -eq 0) {
    $hostnameAdded = Invoke-AzureCustomDomainCommand -Operation 'Checking custom hostname validation (pending DNS is expected on first deployment)' -Arguments (
      @('containerapp', 'hostname', 'add') + $arguments
    )
    if (-not $hostnameAdded) {
      return $false
    }
  }
  $certificateBound = Invoke-AzureCustomDomainCommand -Operation 'Issuing and binding Azure-managed certificate (this can take several minutes)' -Arguments (
    @('containerapp', 'hostname', 'bind') + $arguments +
    @('--environment', $environmentId, '--validation-method', $method)
  )
  return $certificateBound
}

function Complete-AzureDeployment {
  param([Parameter(Mandatory)][hashtable] $Configuration)

  $serviceUri = Test-AzureDeployment -EnvironmentName $Configuration.AZURE_ENV_NAME
  $customDomainReady = Set-AzureCustomDomain -Configuration $Configuration
  while ($Configuration.AZURE_CUSTOM_DOMAIN -and -not $customDomainReady -and $Action -eq 'menu') {
    $script:DomainContinuationPrompted = $true
    if (-not (Read-AzureDomainContinuation)) { break }
    $customDomainReady = Set-AzureCustomDomain -Configuration $Configuration
  }
  if ($Configuration.AZURE_CUSTOM_DOMAIN -and $customDomainReady) {
    $serviceUri = Test-AzureDeployment -EnvironmentName $Configuration.AZURE_ENV_NAME -ServiceUri "https://$($Configuration.AZURE_CUSTOM_DOMAIN)"
  }
  return $serviceUri
}

function Read-AzureDomainContinuation {
  Write-Host ''
  Write-Host 'Press 4 to retry domain setup, or any other key to return to the menu without clearing the output.'
  if ([Console]::IsInputRedirected) {
    $selection = Read-Host
  }
  else {
    $selection = [string] [Console]::ReadKey($true).KeyChar
  }
  return $selection -ceq '4'
}

function Invoke-AzureUp {
  $configuration = Invoke-AzureCheck
  Invoke-ContainerBuild
  Save-AzureDomainBindings -Configuration $configuration
  Invoke-Tool -Executable 'azd' -Operation 'Previewing Azure infrastructure changes' -Arguments @(
    'provision', '--preview', '--environment', $configuration.AZURE_ENV_NAME
  )
  if ($PSCmdlet.ShouldProcess(
      "Azure environment $($configuration.AZURE_ENV_NAME)",
      'Provision billable resources and deploy MockAPI')) {
    Invoke-Tool -Executable 'azd' -Operation 'Provisioning and deploying MockAPI to Azure' -Arguments @(
      'up', '--environment', $configuration.AZURE_ENV_NAME
    )
    $serviceUri = Complete-AzureDeployment -Configuration $configuration
    Write-AzureServiceSummary -ServiceUri $serviceUri
  }
}

function Invoke-AzureDeploy {
  $configuration = Invoke-AzureCheck
  if ($PSCmdlet.ShouldProcess(
      "Azure environment $($configuration.AZURE_ENV_NAME)",
      'Build and deploy the MockAPI container image')) {
    Invoke-Tool -Executable 'azd' -Operation 'Deploying MockAPI to Azure' -Arguments @(
      'deploy', '--environment', $configuration.AZURE_ENV_NAME
    )
    $serviceUri = Complete-AzureDeployment -Configuration $configuration
    Write-AzureServiceSummary -ServiceUri $serviceUri
  }
}

function Invoke-AzurePush {
  $configuration = Invoke-AzureCheck
  if ($PSCmdlet.ShouldProcess(
      "Azure environment $($configuration.AZURE_ENV_NAME)",
      'Build and push the MockAPI container image')) {
    Invoke-Tool -Executable 'azd' -Operation 'Building and pushing MockAPI to Azure Container Registry' -Arguments @(
      'publish', 'mockapi', '--environment', $configuration.AZURE_ENV_NAME, '--no-prompt'
    )
    $serviceUri = Get-AzureServiceUri -EnvironmentName $configuration.AZURE_ENV_NAME
    Write-AzureServiceSummary -ServiceUri $serviceUri
  }
}

function Invoke-AzureImport {
  $configuration = Invoke-AzureCheck
  $environmentName = [string] $configuration.AZURE_ENV_NAME
  $resourceGroupName = "rg-$environmentName-$($configuration.AZURE_LOCATION)"
  $registryEndpoint = Get-AzdEnvironmentValue -EnvironmentName $environmentName -Name 'AZURE_CONTAINER_REGISTRY_ENDPOINT'
  $registryName = $registryEndpoint.Split('.')[0]
  $applicationVersion = Get-ApplicationVersion
  $sourceImage = "docker.io/simonkurtzmsft/mockapi:v$applicationVersion"
  $targetImage = "mockapi:v$applicationVersion"
  $deployedImage = "$registryEndpoint/$targetImage"

  $containerAppResult = Invoke-NativeTool -Executable 'az' -Arguments @(
    'resource', 'list', '--resource-group', $resourceGroupName,
    '--tag', 'azd-service-name=mockapi',
    '--query', "[?type=='Microsoft.App/containerApps'] | [0].name", '--output', 'tsv'
  )
  $containerAppName = ([string] ($containerAppResult.Output | Select-Object -First 1)).Trim()
  if ($containerAppResult.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($containerAppName)) {
    throw "MockAPI Container App was not found in resource group '$resourceGroupName'."
  }

  if ($PSCmdlet.ShouldProcess(
      "Azure environment $environmentName",
      "Import $sourceImage into ACR and deploy it")) {
    Invoke-Tool -Executable 'az' -Operation "Importing $sourceImage into Azure Container Registry" -Arguments @(
      'acr', 'import', '--name', $registryName,
      '--source', $sourceImage, '--image', $targetImage
    )
    Invoke-Tool -Executable 'az' -Operation "Deploying $deployedImage to $containerAppName" -Arguments @(
      'containerapp', 'update', '--name', $containerAppName,
      '--resource-group', $resourceGroupName, '--image', $deployedImage
    )
    $serviceUri = Complete-AzureDeployment -Configuration $configuration
    Write-AzureServiceSummary -ServiceUri $serviceUri
  }
}

function Invoke-AzureDown {
  $configuration = Invoke-AzureCheck
  $resourceGroupName = "rg-$($configuration.AZURE_ENV_NAME)-$($configuration.AZURE_LOCATION)"
  if ($PSCmdlet.ShouldProcess(
      "Azure resource group $resourceGroupName",
      'Delete Azure resources')) {
    Invoke-InteractiveTool -Executable 'az' -Operation 'Requesting asynchronous deletion of MockAPI Azure resources' -Arguments @(
      'group', 'delete', '--name', $resourceGroupName,
      '--subscription', $configuration.AZURE_SUBSCRIPTION_ID, '--no-wait'
    )
  }
}

function Write-Field {
  param(
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $Value,
    [ConsoleColor] $ForegroundColor
  )

  if ($PSBoundParameters.ContainsKey('ForegroundColor')) {
    Write-Host ("{0,-$fieldWidth}: {1}" -f $Name, $Value) -ForegroundColor $ForegroundColor
    return
  }

  Write-Host ("{0,-$fieldWidth}: {1}" -f $Name, $Value)
}

function Reset-ConsoleColors {
  if (-not [Console]::IsOutputRedirected) {
    [Console]::ResetColor()
  }
}

function Invoke-Tool {
  param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string[]] $Arguments,
    [Parameter(Mandatory)][string] $Operation
  )

  Write-Host ''
  Write-Host $Operation -ForegroundColor Cyan
  $result = Invoke-NativeTool -Executable $Executable -Arguments $Arguments -StreamOutput
  if ($result.ExitCode -ne 0) {
    throw "$Operation failed with exit code $($result.ExitCode)."
  }
}

function Invoke-InteractiveTool {
  param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string[]] $Arguments,
    [Parameter(Mandatory)][string] $Operation
  )

  Write-Host ''
  Write-Host $Operation -ForegroundColor Cyan
  $global:LASTEXITCODE = 0
  & $Executable @Arguments
  $exitCode = $global:LASTEXITCODE
  if ($exitCode -ne 0) {
    throw "$Operation failed with exit code $exitCode."
  }
}

function Invoke-NativeTool {
  param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string[]] $Arguments,
    [switch] $StreamOutput,
    [switch] $CaptureStandardError
  )

  $global:LASTEXITCODE = 0
  $output = if ($StreamOutput) {
    @(& $Executable @Arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        $line
      })
  }
  elseif ($CaptureStandardError) {
    @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
  }
  else {
    @(& $Executable @Arguments)
  }
  $exitCode = $global:LASTEXITCODE
  return [pscustomobject]@{
    ExitCode = $exitCode
    Output = $output
  }
}

function Get-ApplicationVersion {
  if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Application project was not found: $projectFile"
  }

  [xml] $project = Get-Content -LiteralPath $projectFile -Raw
  $version = [string] $project.Project.PropertyGroup.Version
  if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Application version was not found in '$projectFile'."
  }
  return $version.Trim()
}

function Install-DotNetSdk {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is missing and winget is unavailable. Install it from https://dotnet.microsoft.com/download/dotnet/10.0.'
  }

  if ($PSCmdlet.ShouldProcess('Microsoft.DotNet.SDK.10', 'Install with winget')) {
    Invoke-Tool -Executable 'winget' -Operation 'Installing .NET 10 SDK' -Arguments @(
      'install', '--id', 'Microsoft.DotNet.SDK.10', '--exact',
      '--accept-package-agreements', '--accept-source-agreements'
    )
  }
}

function Update-ProcessPath {
  $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
  $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  $env:Path = "$machinePath$([IO.Path]::PathSeparator)$userPath"
}

function Install-Azd {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'Azure Developer CLI is missing or outdated and winget is unavailable. Install azd from https://aka.ms/install-azd.'
  }

  $verb = if (Get-Command azd -ErrorAction SilentlyContinue) { 'upgrade' } else { 'install' }
  if ($PSCmdlet.ShouldProcess('Microsoft.Azd', "$verb with winget")) {
    $operation = if ($verb -eq 'upgrade') { 'Upgrading Azure Developer CLI' } else { 'Installing Azure Developer CLI' }
    Invoke-Tool -Executable 'winget' -Operation $operation -Arguments @(
      $verb, '--id', 'Microsoft.Azd', '--exact',
      '--accept-package-agreements', '--accept-source-agreements'
    )
  }
}

function Install-AzureCli {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is missing and winget is unavailable. Install Azure CLI from https://aka.ms/installazurecliwindows.'
  }

  if ($PSCmdlet.ShouldProcess('Microsoft.AzureCLI', 'Install with winget')) {
    Invoke-Tool -Executable 'winget' -Operation 'Installing Azure CLI' -Arguments @(
      'install', '--id', 'Microsoft.AzureCLI', '--exact',
      '--accept-package-agreements', '--accept-source-agreements'
    )
  }
}

function Install-Wslc {
  if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
    throw 'WSLC is missing and WSL is unavailable. Install or update WSL before using container actions.'
  }

  if ($PSCmdlet.ShouldProcess('Windows Subsystem for Linux', 'Update to install WSLC')) {
    Invoke-Tool -Executable 'wsl' -Operation 'Updating WSL and WSLC' -Arguments @('--update')
  }
}

function Assert-DotNet {
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (-not $InstallMissing) {
      throw "The .NET SDK is missing. Run '.\start.ps1 -Action setup -InstallMissing'."
    }
    Install-DotNetSdk
  }

  $versionResult = Invoke-NativeTool -Executable 'dotnet' -Arguments @('--version')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Unable to resolve the .NET SDK version.'
  }
  $resolvedVersion = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
  if ($resolvedVersion -notmatch '^10\.') {
    if ($InstallMissing) {
      Install-DotNetSdk
      $versionResult = Invoke-NativeTool -Executable 'dotnet' -Arguments @('--version')
      if ($versionResult.ExitCode -ne 0) {
        throw 'Unable to resolve the .NET SDK version after installation.'
      }
      $resolvedVersion = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
    }
    if ($resolvedVersion -notmatch '^10\.') {
      throw "Repository requires a .NET 10 SDK, but this directory resolves $resolvedVersion."
    }
  }

  Write-Field '.NET SDK' $resolvedVersion Green
}

function Assert-Wslc {
  if (-not (Get-Command wslc -ErrorAction SilentlyContinue)) {
    if (-not $InstallMissing) {
      throw "WSLC is missing. Update WSL or run '.\start.ps1 -Action setup -InstallMissing'."
    }
    Install-Wslc
  }

  if (-not (Get-Command wslc -ErrorAction SilentlyContinue)) {
    throw 'WSLC is still unavailable after the WSL update. Restart the terminal and run the check again.'
  }

  $versionResult = Invoke-NativeTool -Executable 'wslc' -Arguments @('version')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Unable to resolve the WSLC version.'
  }
  $versionOutput = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
  Write-Field 'WSLC' $versionOutput Green
}

function Assert-Docker {
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is missing. Install Docker Desktop or another Docker CLI and engine, then run the check again.'
  }

  $versionResult = Invoke-NativeTool -Executable 'docker' -Arguments @('version', '--format', '{{.Client.Version}}')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Docker is installed, but its engine is unavailable. Start the Docker engine and run the check again.'
  }
  $versionOutput = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
  Write-Field 'Docker' $versionOutput Green
}

function Assert-ContainerEngine {
  if ($ContainerEngine -eq 'docker') {
    Assert-Docker
    return
  }
  Assert-Wslc
}

function Get-ContainerExecutable {
  return $ContainerEngine
}

function Assert-Pnpm {
  if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    throw "pnpm is missing. Install Node.js from '.nvmrc', enable Corepack, and run 'corepack install'."
  }

  $versionResult = Invoke-NativeTool -Executable 'pnpm' -Arguments @('--version')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Unable to resolve the pnpm version.'
  }
  Write-Field 'pnpm' ([string] ($versionResult.Output | Select-Object -First 1)).Trim() Green
}

function Test-Prerequisites {
  Assert-DotNet
  Assert-Pnpm
  Assert-ContainerEngine
  Write-Field 'Solution' $solutionFile
  Write-Field 'Container engine' $ContainerEngine.ToUpperInvariant()
  Write-Host ''
  Write-Host 'Prerequisite checks passed.' -ForegroundColor Green
}

function Invoke-ToolingRestore {
  Assert-Pnpm
  Invoke-Tool -Executable 'pnpm' -Operation 'Restoring formatting tools' -Arguments @(
    'install', '--frozen-lockfile'
  )
  Invoke-Tool -Executable 'node' -Operation 'Normalizing the pnpm lockfile' -Arguments @(
    '-e', "require('./scripts/lib/pnpm-lock-registry.cjs').normalizeWorkingLockfile()"
  )
  Invoke-Tool -Executable 'git' -Operation 'Installing repository Git hooks' -Arguments @(
    'config', 'core.hooksPath', '.githooks'
  )
}

function Invoke-DependencyUpdates {
  Assert-Pnpm
  Write-Host ''
  Write-Host 'Checking for dependency updates' -ForegroundColor Cyan
  $outdatedResult = Invoke-NativeTool -Executable 'pnpm' -Arguments @('outdated') -StreamOutput
  if ($outdatedResult.ExitCode -gt 1) {
    throw "Dependency update check failed with exit code $($outdatedResult.ExitCode)."
  }
  Invoke-Tool -Executable 'node' -Operation 'Installing the latest stable dependency updates that completed the eight-day cooldown' -Arguments @(
    (Join-Path $repositoryRoot 'scripts/lib/dependency-updater.cjs')
  )
}

function Invoke-PnpmUpdate {
  Assert-Pnpm
  Invoke-Tool -Executable 'node' -Operation 'Updating pnpm to the latest release that completed the eight-day cooldown' -Arguments @(
    (Join-Path $repositoryRoot 'scripts/lib/pnpm-updater.cjs')
  )
}

function Invoke-DependencyToolingUpdates {
  Invoke-DependencyUpdates
  Invoke-PnpmUpdate
}

function Invoke-Format {
  Assert-DotNet
  Assert-Pnpm
  Invoke-Tool -Executable 'pnpm' -Operation 'Formatting repository files' -Arguments @('run', 'format')
}

function Invoke-Lint {
  Assert-DotNet
  Assert-Pnpm
  Invoke-Tool -Executable 'pnpm' -Operation 'Checking repository formatting and Markdown' -Arguments @('run', 'lint')
}

function Invoke-Restore {
  Assert-DotNet
  Invoke-Tool -Executable 'dotnet' -Operation 'Restoring NuGet packages' -Arguments @(
    'restore', $solutionFile
  )
}

function Invoke-DependencySecurity {
  Assert-Pnpm
  Invoke-Tool -Executable 'pnpm' -Operation 'Scanning dependency vulnerabilities' -Arguments @(
    'run', 'validate:dependency-security'
  )
}

function Invoke-Build {
  Assert-DotNet
  Invoke-Tool -Executable 'dotnet' -Operation "Building solution ($Configuration)" -Arguments @(
    'build', $solutionFile, '--configuration', $Configuration,
    '-p:TreatWarningsAsErrors=true'
  )
}

function Get-LocalApplicationUri {
  $configuredUrls = [Environment]::GetEnvironmentVariable('ASPNETCORE_URLS', 'Process')
  if ([string]::IsNullOrWhiteSpace($configuredUrls)) {
    return 'http://localhost:5000/'
  }

  $configuredUrl = @($configuredUrls.Split(';') | Where-Object {
      $_ -match '^https?://'
    } | Select-Object -First 1)
  if ($configuredUrl.Count -eq 0) {
    throw 'ASPNETCORE_URLS must contain at least one HTTP or HTTPS URL.'
  }

  $browserUrl = $configuredUrl[0] -replace '://(?:\*|\+|0\.0\.0\.0|\[::\])(?=[:/]|$)', '://localhost'
  try {
    $uri = [Uri] $browserUrl
  }
  catch {
    throw "ASPNETCORE_URLS contains an invalid URL '$($configuredUrl[0])'."
  }
  return "$($uri.GetLeftPart([UriPartial]::Authority))/"
}

function Start-BrowserWhenReady {
  param([Parameter(Mandatory)][string] $ApplicationUri)

  return Start-Job -ScriptBlock {
    param($Uri)

    $readinessUri = [Uri]::new([Uri] $Uri, '/health/ready')
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
      try {
        $response = Invoke-WebRequest -Uri $readinessUri -SkipCertificateCheck -TimeoutSec 1
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
          Start-Process -FilePath $Uri
          return
        }
      }
      catch {
        Start-Sleep -Milliseconds 500
      }
    }

    Write-Warning "MockAPI did not become ready at '$readinessUri'; the browser was not opened."
  } -ArgumentList $ApplicationUri
}

function Invoke-Run {
  param([switch] $ShowTutorial)

  Assert-DotNet
  $tutorialMode = if ($ShowTutorial) { 'show' } else { 'skip' }
  $applicationUri = "$(Get-LocalApplicationUri)?tutorial=$tutorialMode"
  $browserJob = Start-BrowserWhenReady -ApplicationUri $applicationUri
  try {
    Write-Host ''
    Write-Host 'Running MockAPI' -ForegroundColor Cyan
    Write-Host ''
    $result = Invoke-NativeTool -Executable 'dotnet' -Arguments @(
      'run', '--project', $projectFile, '--configuration', $Configuration
    ) -StreamOutput
    if ($result.ExitCode -ne 0) {
      throw "Running MockAPI failed with exit code $($result.ExitCode)."
    }
  }
  finally {
    if ($browserJob.State -eq 'Running') {
      Stop-Job -Job $browserJob
    }
    Receive-Job -Job $browserJob
    Remove-Job -Job $browserJob -Force
  }
}

function Invoke-Tests {
  Assert-DotNet
  Assert-Pnpm
  Invoke-Tool -Executable 'dotnet' -Operation "Running tests ($Configuration)" -Arguments @(
    'test', $solutionFile, '--configuration', $Configuration,
    '-p:TreatWarningsAsErrors=true'
  )
  Invoke-Tool -Executable 'pnpm' -Operation 'Running frontend unit tests' -Arguments @(
    'run', 'test:frontend'
  )
  Invoke-Tool -Executable 'pwsh' -Operation 'Running Developer CLI tests' -Arguments @(
    '-NoProfile', '-File', (Join-Path $repositoryRoot 'tests/DeveloperCli.Tests.ps1')
  )
}

function Invoke-Coverage {
  Assert-DotNet
  New-Item -ItemType Directory -Path $coverageDirectory -Force | Out-Null
  Invoke-Tool -Executable 'dotnet' -Operation 'Running tests with XPlat code coverage' -Arguments @(
    'test', $solutionFile, '--configuration', $Configuration,
    '--settings', (Join-Path $repositoryRoot 'tests/coverage.runsettings'),
    '--collect:XPlat Code Coverage', '--results-directory', $coverageDirectory,
    '-p:TreatWarningsAsErrors=true'
  )

  $coverageFile = Get-ChildItem -LiteralPath $coverageDirectory -Filter 'coverage.cobertura.xml' -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
  if ($null -eq $coverageFile) {
    throw "Tests passed but no Cobertura coverage file was found under '$coverageDirectory'."
  }
  Write-Field 'Coverage report' $coverageFile.FullName Green
  & (Join-Path $repositoryRoot 'scripts/Assert-Coverage.ps1') -Report $coverageFile.FullName
  if (-not $?) {
    throw 'Backend coverage validation failed.'
  }
  Invoke-Tool -Executable 'pnpm' -Operation 'Running frontend tests with coverage' -Arguments @(
    'run', 'test:frontend:coverage'
  )
}

function Invoke-Publish {
  Assert-DotNet
  if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
  }

  foreach ($runtime in @('linux-musl-x64', 'linux-musl-arm64')) {
    $outputDirectory = Join-Path $publishDirectory $runtime
    Invoke-Tool -Executable 'dotnet' -Operation "Publishing $runtime ($Configuration)" -Arguments @(
      'publish', $projectFile, '--configuration', $Configuration, '--runtime', $runtime,
      '--output', $outputDirectory, '-p:PublishAot=false', '-p:TreatWarningsAsErrors=true'
    )
    & (Join-Path $repositoryRoot 'scripts/Assert-PublishContents.ps1') -PublishDirectory $outputDirectory
    if (-not $?) {
      throw "Publish content validation failed for $runtime."
    }
  }
}

function Assert-ContainerInputs {
  Assert-ContainerEngine
  if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
    throw "Container definition was not found: $dockerfile"
  }
}

function Get-Container {
  $result = Invoke-NativeTool -Executable (Get-ContainerExecutable) -Arguments @('inspect', $ContainerName) 2>$null
  if ($result.ExitCode -ne 0) {
    return $null
  }

  $containers = @($result.Output -join [Environment]::NewLine | ConvertFrom-Json)
  if ($containers.Count -eq 0) {
    return $null
  }
  return $containers[0]
}

function Get-ContainerImageId {
  $result = Invoke-NativeTool -Executable (Get-ContainerExecutable) -Arguments @('image', 'inspect', $ImageName) 2>$null
  if ($result.ExitCode -ne 0) {
    throw "Container image '$ImageName' does not exist. Run '.\start.ps1 -Action container-build'."
  }

  $images = @($result.Output -join [Environment]::NewLine | ConvertFrom-Json)
  if ($images.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string] $images[0].Id)) {
    throw "Container image '$ImageName' does not expose an image ID."
  }
  return ([string] $images[0].Id).Trim()
}

function Get-ContainerRecordedImageId {
  param([Parameter(Mandatory)] $Container)

  if ($null -eq $Container.Labels) {
    return $null
  }
  $property = $Container.Labels.PSObject.Properties[$containerImageIdLabel]
  if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string] $property.Value)) {
    return $null
  }
  return ([string] $property.Value).Trim()
}

function Get-ImageRepository {
  $lastSlash = $ImageName.LastIndexOf('/')
  $lastColon = $ImageName.LastIndexOf(':')
  if ($lastColon -gt $lastSlash) {
    return $ImageName.Substring(0, $lastColon)
  }
  return $ImageName
}

function New-BuildImageName {
  param(
    [Parameter(Mandatory)][DateTimeOffset] $Timestamp,
    [string] $Suffix
  )

  $repository = Get-ImageRepository
  $tag = 'build-' + $Timestamp.ToUniversalTime().ToString(
    'yyyyMMddTHHmmssZ',
    [Globalization.CultureInfo]::InvariantCulture
  )
  if (-not [string]::IsNullOrWhiteSpace($Suffix)) {
    $tag += "-$Suffix"
  }
  return "${repository}:$tag"
}

function Get-ContainerBuildNuGetSource {
  if (-not [string]::IsNullOrWhiteSpace($NuGetSource)) {
    $source = $NuGetSource.Trim()
  }
  else {
    $source = 'https://api.nuget.org/v3/index.json'
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
      $sourceResult = Invoke-NativeTool -Executable 'dotnet' -Arguments @(
        'nuget', 'list', 'source', '--format', 'Short'
      )
      if ($sourceResult.ExitCode -eq 0) {
        $enabledSource = @($sourceResult.Output | Where-Object { $_ -match '^E\s+(?<source>https://\S+)\s*$' } | Select-Object -First 1)
        if ($enabledSource.Count -ne 0 -and $enabledSource[0] -match '^E\s+(?<source>https://\S+)\s*$') {
          $source = $Matches.source
        }
      }
    }
  }

  $sourceUri = $null
  if (-not [Uri]::TryCreate($source, [UriKind]::Absolute, [ref] $sourceUri) -or
      $sourceUri.Scheme -ne [Uri]::UriSchemeHttps -or
      -not [string]::IsNullOrEmpty($sourceUri.UserInfo)) {
    throw 'The NuGet source for container builds must be an absolute HTTPS URL without embedded credentials.'
  }
  return $sourceUri.AbsoluteUri
}

function Add-BuildTagToCurrentImage {
  $containerExecutable = Get-ContainerExecutable
  $result = Invoke-NativeTool -Executable $containerExecutable -Arguments @('image', 'inspect', $ImageName) 2>$null
  if ($result.ExitCode -ne 0) {
    return
  }

  $images = @($result.Output -join [Environment]::NewLine | ConvertFrom-Json)
  if ($images.Count -eq 0) {
    return
  }
  $image = $images[0]
  $buildTagPrefix = "$(Get-ImageRepository):build-"
  $hasBuildTag = @($image.RepoTags | Where-Object {
      ([string] $_).StartsWith($buildTagPrefix, [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
  if ($hasBuildTag) {
    return
  }

  $created = [DateTimeOffset]::Parse(
    [string] $image.Created,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal
  )
  $shortImageId = ([string] $image.Id).Replace('sha256:', '').Substring(0, 12)
  $buildImageName = New-BuildImageName -Timestamp $created -Suffix $shortImageId
  Invoke-Tool -Executable $containerExecutable -Operation "Preserving current image as $buildImageName" -Arguments @(
    'image', 'tag', $ImageName, $buildImageName
  )
}

function Test-ContainerVolumeExists {
  $result = Invoke-NativeTool -Executable (Get-ContainerExecutable) -Arguments @('volume', 'inspect', $VolumeName) 2>$null
  return $result.ExitCode -eq 0
}

function Initialize-ContainerVolumePermissions {
  $containerExecutable = Get-ContainerExecutable
  $maintenanceContainerName = "$ContainerName-volume-init-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
  try {
    Invoke-Tool -Executable $containerExecutable -Operation "Initializing volume permissions for $VolumeName" -Arguments @(
      'run', '--name', $maintenanceContainerName,
      '--user', 'root', '--entrypoint', 'chown',
      "--volume=${VolumeName}:/data",
      $ImageName, '1654:1654', '/data'
    )
  }
  finally {
    $maintenanceContainer = Invoke-NativeTool -Executable $containerExecutable -Arguments @(
      'inspect', $maintenanceContainerName
    ) 2>$null
    if ($maintenanceContainer.ExitCode -eq 0) {
      $removeArguments = if ($ContainerEngine -eq 'docker') {
        @('rm', '--force', $maintenanceContainerName)
      }
      else {
        @('remove', '--force', $maintenanceContainerName)
      }
      $removeResult = Invoke-NativeTool -Executable $containerExecutable -StreamOutput -Arguments $removeArguments
      if ($removeResult.ExitCode -ne 0) {
        Write-Warning "Unable to remove maintenance container '$maintenanceContainerName'."
      }
    }
  }
}

function ConvertFrom-SecureStringToPlainText {
  param([Parameter(Mandatory)][Security.SecureString] $SecureString)

  $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
  try {
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
  }
  finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
  }
}

function New-DashboardPasswordHash {
  param([Parameter(Mandatory)][Security.SecureString] $Password)

  $iterations = 600000
  $salt = [byte[]]::new(16)
  [Security.Cryptography.RandomNumberGenerator]::Fill($salt)
  $plainText = ConvertFrom-SecureStringToPlainText -SecureString $Password
  try {
    $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
      $plainText,
      $salt,
      $iterations,
      [Security.Cryptography.HashAlgorithmName]::SHA256,
      32
    )
    return 'v1.{0}.{1}.{2}' -f @(
      $iterations,
      [Convert]::ToBase64String($salt),
      [Convert]::ToBase64String($hash)
    )
  }
  finally {
    $plainText = $null
  }
}

function Read-DashboardCredential {
  Write-Host ''
  $username = (Read-Host 'Dashboard username (leave blank to disable authentication)').Trim()
  if ([string]::IsNullOrWhiteSpace($username)) {
    return $null
  }
  if ($username.Contains(':')) {
    throw 'Dashboard username cannot contain a colon.'
  }

  $password = Read-Host 'Dashboard password (minimum 8 characters)' -AsSecureString
  $confirmation = Read-Host 'Confirm dashboard password' -AsSecureString
  $plainText = ConvertFrom-SecureStringToPlainText -SecureString $password
  $confirmationText = ConvertFrom-SecureStringToPlainText -SecureString $confirmation
  try {
    if ($plainText.Length -lt 8) {
      throw 'Dashboard password must contain at least 8 characters.'
    }
    if (-not $plainText.Equals($confirmationText, [StringComparison]::Ordinal)) {
      throw 'Dashboard passwords do not match.'
    }
  }
  finally {
    $plainText = $null
    $confirmationText = $null
  }

  return [pscustomobject]@{
    Username = $username
    Password = $password
    PasswordHash = New-DashboardPasswordHash -Password $password
  }
}

function Get-WslcBuildFailureMessage {
  param([Parameter(Mandatory)][object[]] $Output)

  $buildOutput = $Output -join [Environment]::NewLine
  $gitMetadataWarning = $buildOutput -match 'git was not found in the system'

  if ($buildOutput -match 'network is unreachable|dial tcp .*connect: network') {
    $hostCanReachRegistry = Test-NetConnection mcr.microsoft.com -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue
    $message = @(
      'WSLC could not reach Microsoft Container Registry over HTTPS. This is a WSLC/WSL container-network failure, not a missing image tag.'
      $(if ($hostCanReachRegistry) {
          'Windows can reach mcr.microsoft.com:443, which isolates the failure to the WSLC/WSL container network.'
        }
        else {
          'Windows also cannot reach mcr.microsoft.com:443, so check host networking before WSLC-specific recovery.'
        })
      'Verify the engine path directly with:'
      '  wslc pull mcr.microsoft.com/dotnet/sdk:10.0.400-alpine3.24@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1'
      '  wslc pull mcr.microsoft.com/dotnet/runtime-deps:10.0.11-alpine3.24@sha256:379b17d7d388a2a1b5330bfc2429a01091f85e255d3bce7981d65927d786c000'
      "If those fail, run 'wslc system session terminate' to recreate WSLC's independent network session, then retry the pulls."
      "A stale WSLC session survives 'wsl --shutdown', so shutting down ordinary WSL alone may not repair this failure."
      'If a fresh WSLC session still fails, check VPN, proxy, firewall, and WSL networking policy.'
    )
    if ($gitMetadataWarning) {
      $message += 'The Git message is a separate, non-fatal source-metadata warning; it did not stop this build.'
    }
    return $message -join [Environment]::NewLine
  }

  if ($buildOutput -match 'manifest unknown|not found') {
    return @(
      'WSLC reached the registry, but a pinned base-image manifest was not found.'
      'Check the Dockerfile tags against Microsoft Container Registry before changing them.'
    ) -join [Environment]::NewLine
  }

  if ($gitMetadataWarning) {
    return 'The Git source-metadata warning is non-fatal. Review the other WSLC build errors above for the actual failure.'
  }

  return 'Review the WSLC build output above for the underlying container-engine failure.'
}

function Invoke-ContainerBuild {
  Assert-ContainerInputs
  $containerExecutable = Get-ContainerExecutable
  $containerBuildNuGetSource = Get-ContainerBuildNuGetSource
  Add-BuildTagToCurrentImage
  $buildImageName = New-BuildImageName -Timestamp ([DateTimeOffset]::UtcNow)
  Write-Host ''
  Write-Host "Building native container image $buildImageName" -ForegroundColor Cyan
  $buildArguments = [Collections.Generic.List[string]]::new()
  $buildArguments.AddRange([string[]] @('build', '--pull'))
  $buildArguments.AddRange([string[]] @('--tag', $buildImageName))
  $buildArguments.AddRange([string[]] @('--build-arg', "NUGET_SOURCE=$containerBuildNuGetSource"))
  $buildArguments.Add($repositoryRoot)
  Write-Field 'NuGet source' $containerBuildNuGetSource
  $buildResult = Invoke-NativeTool -Executable $containerExecutable -StreamOutput -Arguments $buildArguments.ToArray()
  if ($buildResult.ExitCode -ne 0) {
    $diagnosis = if ($ContainerEngine -eq 'wslc') {
      Get-WslcBuildFailureMessage -Output $buildResult.Output
    }
    else {
      'Review the Docker build output above for the underlying container-engine failure.'
    }
    throw "Building native container image $buildImageName failed with exit code $($buildResult.ExitCode).`n`n$diagnosis"
  }
  Invoke-Tool -Executable $containerExecutable -Operation "Moving image alias $ImageName to $buildImageName" -Arguments @(
    'image', 'tag', $buildImageName, $ImageName
  )
  Write-Field 'Build image' $buildImageName Green
  Write-Field 'Current alias' $ImageName Green
  Write-Host 'Removing dashboard assets makes only a very small difference to the image size.' -ForegroundColor DarkGray
  Write-Host 'Set MockApi__EnableDashboard=false to disable the dashboard when it should not be deployed.' -ForegroundColor DarkGray
}

function Invoke-ContainerRun {
  Assert-ContainerInputs
  $containerExecutable = Get-ContainerExecutable
  $currentImageId = Get-ContainerImageId
  $container = Get-Container
  if ($null -ne $container) {
    $containerImageId = Get-ContainerRecordedImageId -Container $container
    if ([string]::IsNullOrWhiteSpace($containerImageId) -or
        -not $containerImageId.Equals($currentImageId, [StringComparison]::OrdinalIgnoreCase)) {
      $reason = if ([string]::IsNullOrWhiteSpace($containerImageId)) {
        'does not record its image ID'
      }
      else {
        "uses image $containerImageId instead of $currentImageId"
      }
      Write-Field 'Container' "$ContainerName $reason; recreating" Yellow
      $removeArguments = if ($ContainerEngine -eq 'docker') { @('rm', '--force', $ContainerName) } else { @('remove', '--force', $ContainerName) }
      Invoke-Tool -Executable $containerExecutable -Operation "Removing stale container $ContainerName" -Arguments $removeArguments
      $container = $null
    }
  }
  if ($null -ne $container) {
    if ([bool] $container.State.Running) {
      Write-Field 'Container' "$ContainerName is already running" Green
      Invoke-ContainerTest -SkipPrerequisiteCheck
      Write-Field 'Application URL' "http://localhost:$Port" Green
      return
    }

    Invoke-Tool -Executable $containerExecutable -Operation "Restarting container $ContainerName" -Arguments @(
      'start', $ContainerName
    )
    Invoke-ContainerTest -SkipPrerequisiteCheck
    Write-Field 'Application URL' "http://localhost:$Port" Green
    return
  }
  if (-not (Test-ContainerVolumeExists)) {
    Invoke-Tool -Executable $containerExecutable -Operation "Creating volume $VolumeName" -Arguments @(
      'volume', 'create', $VolumeName
    )
  }
  Initialize-ContainerVolumePermissions
  $dashboardCredential = Read-DashboardCredential
  $containerArguments = [Collections.Generic.List[string]]::new()
  $containerArguments.AddRange([string[]] @(
      'run', '--detach', '--name', $ContainerName,
      '--label', "${containerImageIdLabel}=${currentImageId}",
      '--cpus', '0.5', '--memory', '256M',
      '--publish', "${Port}:8080"
    ))
  $containerArguments.Add("--volume=${VolumeName}:/data")
  if ($null -ne $dashboardCredential) {
    $containerArguments.Add('--env')
    $containerArguments.Add("MockApi__DashboardUsername=$($dashboardCredential.Username)")
    $containerArguments.Add('--env')
    $containerArguments.Add("MockApi__DashboardPasswordHash=$($dashboardCredential.PasswordHash)")
  }
  $containerArguments.Add($ImageName)

  Write-Host ''
  if ($ContainerEngine -eq 'wslc') {
    Write-Host 'WSLC may warn that this WSL kernel cannot limit swap separately. The 256 MiB memory limit remains active.' -ForegroundColor DarkYellow
  }
  Invoke-Tool -Executable $containerExecutable -Operation "Starting container $ContainerName" -Arguments $containerArguments.ToArray()
  Invoke-ContainerTest -SkipPrerequisiteCheck -DashboardCredential $dashboardCredential
  Write-Field 'Application URL' "http://localhost:$Port" Green
}

function Invoke-ContainerTest {
  param(
    [switch] $SkipPrerequisiteCheck,
    $DashboardCredential
  )

  if (-not $SkipPrerequisiteCheck) {
    Assert-ContainerEngine
  }
  $container = Get-Container
  if ($null -eq $container) {
    throw "Container '$ContainerName' does not exist. Run the container first."
  }
  if (-not [bool] $container.State.Running) {
    throw "Container '$ContainerName' is stopped (status: $($container.State.Status), exit code: $($container.State.ExitCode)). Run '.\start.ps1 -Action container-run' to restart it."
  }

  $uri = "http://localhost:$Port/health/ready"
  $attemptCount = 10
  $response = $null
  $lastFailure = $null
  for ($attempt = 1; $attempt -le $attemptCount; $attempt++) {
    try {
      $response = Invoke-WebRequest -Uri $uri -TimeoutSec 2 -SkipHttpErrorCheck
      break
    }
    catch {
      $lastFailure = $_.Exception.Message
      $container = Get-Container
      if ($null -eq $container -or -not [bool] $container.State.Running) {
        $status = if ($null -eq $container) { 'missing' } else { [string] $container.State.Status }
        throw "Container '$ContainerName' stopped while waiting for '$uri' (status: $status). Review '.\start.ps1 -Action container-logs'."
      }
      if ($attempt -lt $attemptCount) {
        Start-Sleep -Milliseconds 500
      }
    }
  }
  if ($null -eq $response) {
    throw "Container smoke test failed for '$uri' after $attemptCount attempts. Review '.\start.ps1 -Action container-logs'. $lastFailure"
  }
  if ($response.StatusCode -ne 200) {
    throw "Container smoke test expected readiness to return HTTP 200 but received $($response.StatusCode)."
  }
  if ($response.Content -notmatch '"status"\s*:\s*"ready"') {
    throw "Container smoke test received an unexpected readiness response body."
  }
  Write-Field 'Smoke test' "$uri -> HTTP $($response.StatusCode)" Green

  $dashboardUri = "http://localhost:$Port/"
  $dashboardHeaders = @{}
  if ($null -ne $DashboardCredential) {
    $password = ConvertFrom-SecureStringToPlainText -SecureString $DashboardCredential.Password
    try {
      $encodedCredential = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes("$($DashboardCredential.Username):$password")
      )
      $dashboardHeaders.Authorization = "Basic $encodedCredential"
    }
    finally {
      $password = $null
    }
  }
  $dashboard = Invoke-WebRequest -Uri $dashboardUri -Headers $dashboardHeaders -TimeoutSec 2 -SkipHttpErrorCheck
  if ($dashboard.StatusCode -eq 401 -and $null -eq $DashboardCredential) {
    Write-Field 'Dashboard' "$dashboardUri -> HTTP 401 (authentication enabled)" Green
    return
  }
  if ($dashboard.StatusCode -ne 200 -or $dashboard.Content -notmatch '<title>MockAPI') {
    throw "Container smoke test expected the administrative dashboard at '$dashboardUri'."
  }
  Write-Field 'Dashboard' "$dashboardUri -> HTTP $($dashboard.StatusCode)" Green
}

function Invoke-ContainerShowcase {
  if (-not $SkipContainerCheck) {
    Assert-ContainerEngine
    $container = Get-Container
    if ($null -eq $container -or -not [bool] $container.State.Running) {
      throw "Container '$ContainerName' is not running. Run '.\start.ps1 -Action container-run' first."
    }
  }

  $baseUri = "http://localhost:$Port"
  $configurationUri = "$baseUri/__mockapi/api/configuration"
  $endpointsUri = "$baseUri/__mockapi/api/endpoints"
  $statisticsUri = "$baseUri/__mockapi/api/statistics"
  $exampleUri = "$baseUri/ex/rate-limited"
  $endpointId = '7b2d425d-75f1-4ded-a74e-503374a7e99e'
  try {
    $endpointsResponse = Invoke-WebRequest -Uri $endpointsUri -TimeoutSec 5 -SkipHttpErrorCheck
    if ($endpointsResponse.StatusCode -eq 401) {
      throw "Dashboard authentication is enabled. Load examples in the dashboard at $baseUri, then rerun the showcase."
    }
    if ($endpointsResponse.StatusCode -ne 200) {
      throw "Reading the active endpoints returned HTTP $($endpointsResponse.StatusCode)."
    }
    $activeEndpoints = @($endpointsResponse.Content | ConvertFrom-Json)
    $showcaseEndpoint = $activeEndpoints | Where-Object {
      $null -ne $_ -and
      $null -ne $_.PSObject.Properties['id'] -and
      [string] $_.id -eq $endpointId
    }
    if (-not $showcaseEndpoint) {
      $configuration = Invoke-WebRequest -Uri $configurationUri -TimeoutSec 5 -SkipHttpErrorCheck
      if ($configuration.StatusCode -eq 401) {
        throw "Dashboard authentication is enabled. Load examples in the dashboard at $baseUri, then rerun the showcase."
      }
      if ($configuration.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace([string] $configuration.Headers.ETag)) {
        throw "The management API did not return the current configuration ETag."
      }

      $merge = Invoke-WebRequest `
        -Uri "$configurationUri/example/merge" `
        -Method Post `
        -Headers @{ 'If-Match' = [string] $configuration.Headers.ETag } `
        -TimeoutSec 5 `
        -SkipHttpErrorCheck
      if ($merge.StatusCode -eq 409) {
        throw "The built-in examples conflict with the active configuration. Resolve the conflicts from the dashboard at $baseUri, then rerun the showcase."
      }
      if ($merge.StatusCode -ne 200) {
        throw "Loading the built-in examples returned HTTP $($merge.StatusCode)."
      }
      Write-Field 'Examples' 'Loaded built-in examples' Green
    }
  }
  catch {
    throw "Container '$ContainerName' could not prepare the built-in example. $($_.Exception.Message)"
  }

  $before = Invoke-RestMethod -Uri $statisticsUri -TimeoutSec 5
  $beforeEndpoint = @($before.endpoints | Where-Object { [string] $_.endpointId -eq $endpointId } | Select-Object -First 1)
  $beforeEndpointTotal = if ($beforeEndpoint.Count -eq 0) { 0L } else { [long] $beforeEndpoint[0].totalRequests }
  $checks = [Collections.Generic.List[object]]::new()

  function Add-ShowcaseCheck {
    param(
      [Parameter(Mandatory)][string] $Name,
      [Parameter(Mandatory)][bool] $Passed,
      [Parameter(Mandatory)][string] $Detail
    )
    $checks.Add([pscustomobject]@{
        Check = $Name
        Result = if ($Passed) { 'PASS' } else { 'FAIL' }
        Detail = $Detail
      })
  }

  $httpClient = [Net.Http.HttpClient]::new()
  $httpClient.Timeout = [TimeSpan]::FromSeconds(5)
  $response = $null
  $rateLimitRequestCount = 0
  try {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
      $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $exampleUri)
      $request.Version = [Version]::new(1, 1)
      $request.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
      try {
        $response = $httpClient.Send($request)
      }
      finally {
        $request.Dispose()
      }
      $rateLimitRequestCount++
      if ([int] $response.StatusCode -eq 404) {
        throw 'The built-in rate-limit example is unavailable after loading the examples.'
      }
      if ([int] $response.StatusCode -eq 429) {
        break
      }
      $response.Dispose()
      $response = $null
    }

    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    [Collections.Generic.IEnumerable[string]] $sourceHeaderValues = $null
    [Collections.Generic.IEnumerable[string]] $retryAfterHeaderValues = $null
    $sourceValues = if ($response.Headers.TryGetValues('X-Mock-Source', [ref] $sourceHeaderValues)) {
      [string]::Join(',', $sourceHeaderValues)
    }
    else { '' }
    $retryAfter = if ($response.Headers.TryGetValues('Retry-After', [ref] $retryAfterHeaderValues)) {
      [string]::Join(',', $retryAfterHeaderValues)
    }
    else { '' }
    $contentType = [string] $response.Content.Headers.ContentType
    Add-ShowcaseCheck '429 status' ([int] $response.StatusCode -eq 429) "HTTP $([int] $response.StatusCode)"
    Add-ShowcaseCheck 'Reason phrase' ($response.ReasonPhrase -ceq 'Too Many Requests') ([string] $response.ReasonPhrase)
    Add-ShowcaseCheck 'Retry-After' ($retryAfter -eq '10') $retryAfter
    Add-ShowcaseCheck 'Repeated headers' ($sourceValues.Contains('MockAPI') -and $sourceValues.Contains('checked-in-example')) $sourceValues
    Add-ShowcaseCheck 'Content type' ($contentType -eq 'application/json; charset=utf-8') $contentType
    Add-ShowcaseCheck 'Exact body' ($responseBody -ceq '{"error":"try again later"}') $responseBody
  }
  catch {
    throw "Container '$ContainerName' is not responding correctly at '$exampleUri'. Run '.\start.ps1 -Action container-logs' and verify the configured port. $($_.Exception.Message)"
  }
  finally {
    if ($null -ne $response) {
      $response.Dispose()
    }
    $httpClient.Dispose()
  }

  $queryResponse = Invoke-WebRequest -Uri "$exampleUri`?request=showcase" -TimeoutSec 5 -SkipHttpErrorCheck
  Add-ShowcaseCheck 'Query-insensitive match' ($queryResponse.StatusCode -eq 429) "HTTP $($queryResponse.StatusCode)"
  $wrongMethod = Invoke-WebRequest -Uri $exampleUri -Method Post -TimeoutSec 5 -SkipHttpErrorCheck
  Add-ShowcaseCheck 'Unsupported method' ($wrongMethod.StatusCode -eq 404) "HTTP $($wrongMethod.StatusCode)"
  $unmatched = Invoke-WebRequest -Uri "$baseUri/ex/not-configured" -TimeoutSec 5 -SkipHttpErrorCheck
  Add-ShowcaseCheck 'Unmatched path' ($unmatched.StatusCode -eq 404) "HTTP $($unmatched.StatusCode)"

  $after = Invoke-RestMethod -Uri $statisticsUri -TimeoutSec 5
  $afterEndpoint = @($after.endpoints | Where-Object { [string] $_.endpointId -eq $endpointId } | Select-Object -First 1)
  $afterEndpointTotal = if ($afterEndpoint.Count -eq 0) { 0L } else { [long] $afterEndpoint[0].totalRequests }
  $expectedTotalRequests = $rateLimitRequestCount + 3
  $expectedMatchedRequests = $rateLimitRequestCount + 1
  Add-ShowcaseCheck 'Aggregate statistics' (
    ([long] $after.totalRequests - [long] $before.totalRequests) -eq $expectedTotalRequests -and
    ([long] $after.matchedRequests - [long] $before.matchedRequests) -eq $expectedMatchedRequests -and
    ([long] $after.unmatchedRequests - [long] $before.unmatchedRequests) -eq 2
  ) "total +$([long] $after.totalRequests - [long] $before.totalRequests), matched +$([long] $after.matchedRequests - [long] $before.matchedRequests), unmatched +$([long] $after.unmatchedRequests - [long] $before.unmatchedRequests)"
  Add-ShowcaseCheck 'Endpoint statistics' (($afterEndpointTotal - $beforeEndpointTotal) -eq $expectedMatchedRequests) "endpoint +$($afterEndpointTotal - $beforeEndpointTotal)"

  Write-Host ''
  Write-Host 'Built-in example showcase' -ForegroundColor Cyan
  $checks | Format-Table -AutoSize | Out-Host
  $failed = @($checks | Where-Object Result -eq 'FAIL')
  if ($failed.Count -ne 0) {
    throw "$($failed.Count) showcase assertion(s) failed."
  }
  Write-Field 'Showcase' 'All assertions passed' Green
}

function Invoke-ContainerLogs {
  Assert-ContainerEngine
  Invoke-Tool -Executable (Get-ContainerExecutable) -Operation "Showing logs for $ContainerName" -Arguments @(
    'logs', '--tail', '200', $ContainerName
  )
}

function Invoke-ContainerStatus {
  Assert-ContainerEngine
  Invoke-Tool -Executable (Get-ContainerExecutable) -Operation "Inspecting $ContainerName" -Arguments @(
    'inspect', $ContainerName
  )
}

function Invoke-ContainerStop {
  Assert-ContainerEngine
  Invoke-Tool -Executable (Get-ContainerExecutable) -Operation "Stopping $ContainerName" -Arguments @(
    'stop', '--time', '1', $ContainerName
  )
}

function Invoke-ContainerRemove {
  Assert-ContainerEngine
  if ($PSCmdlet.ShouldProcess($ContainerName, "Remove $ContainerEngine container")) {
    $removeArguments = if ($ContainerEngine -eq 'docker') { @('rm', $ContainerName) } else { @('remove', $ContainerName) }
    Invoke-Tool -Executable (Get-ContainerExecutable) -Operation "Removing $ContainerName" -Arguments $removeArguments
  }
}

function Invoke-Setup {
  Test-Prerequisites
  Invoke-ToolingRestore
  Invoke-Restore
  Write-Host ''
  Write-Host 'Local development setup is ready.' -ForegroundColor Green
}

function Invoke-Validation {
  Invoke-ToolingRestore
  Invoke-Restore
  Invoke-DependencySecurity
  Invoke-Lint
  Invoke-Build
  Invoke-Tests
  Invoke-Coverage
  Invoke-Publish
  Write-Host ''
  Write-Host 'Managed-code validation passed.' -ForegroundColor Green
}

function Invoke-All {
  Invoke-Validation
  Invoke-ContainerBuild
  Write-Host ''
  Write-Host 'Full local validation and native container build passed.' -ForegroundColor Green
}

function Invoke-InitialAzurePathway {
  Write-Host '1/3: Test' -ForegroundColor Cyan
  Invoke-Tests
  Write-Host '2/3: Build' -ForegroundColor Cyan
  Invoke-Build
  Write-Host '3/3: Provision and deploy' -ForegroundColor Cyan
  Invoke-AzureUp
}

function Invoke-UpdateAzurePathway {
  Write-Host '1/3: Test' -ForegroundColor Cyan
  Invoke-Tests
  Write-Host '2/3: Build' -ForegroundColor Cyan
  Invoke-Build
  Write-Host '3/3: Build, push, and deploy image' -ForegroundColor Cyan
  Invoke-AzureDeploy
}

function Show-Help {
  Write-Host @"
MockAPI Developer CLI

Usage:
  .\start.ps1 -Action <action> [options]

Interactive menu (no action required):
  Run locally, Verify, Azure, Containers, Setup, Help, Quit.
  Choose a group to see its actions; use b to go back.
  Existing action shortcuts (such as p1, a4, c3) work from any menu.


1) Pathways

   pathway-azure-initial  Test, build, and deploy the initial Azure environment.
   pathway-azure-update   Test, build, and update an existing Azure deployment.


2) Setup

   check              Verify the pinned .NET SDK, pnpm, and selected container engine.
   setup              Check prerequisites and restore development dependencies.
   dependencies-update Check for and install pnpm dependency updates that completed the eight-day cooldown.
   pnpm-update         Update the project pnpm pin to the latest release that completed the cooldown.
   restore            Restore NuGet packages.


3) Local Development

   run                Run locally and open the dashboard without the tutorial.
   run-tutorial       Run locally and open the dashboard with the tutorial.


4) Verification

   format             Format supported repository files and managed code.
   lint               Check formatting, Markdown, and managed code style.
   build              Build with warnings treated as errors.
   test               Run all tests.
   coverage           Run tests and write Cobertura output under artifacts/coverage.
   publish            Cross-publish trimmed CoreCLR artifacts for managed-code checks, not release images.
   validate           Restore, build, test, collect coverage, and publish.
   all                Run managed validation and build the native container image.


5) Container Operations (WSLC or Docker)

   container-build    Build a native AOT image and move the $ImageName alias to its unique build tag.
                      Removing dashboard assets saves very little image space; set
                      MockApi__EnableDashboard=false to disable the dashboard at runtime.
   container-run      Create or start $ContainerName; full-mode containers prompt for optional dashboard credentials.
   container-test     Send an HTTP smoke test to the running container.
   container-showcase Load the built-in examples when needed, then verify rate-limit response and statistics behavior.
   container-logs     Show the last 200 container log lines.
   container-status   Inspect the container.
   container-stop     Stop the container with a one-second graceful shutdown window.
   container-remove   Remove the stopped container; the data volume is retained.


6) Azure (Container Apps via azd)

   azure-setup        Verify Azure tooling; use -InstallMissing to install or update it.
   azure-check        Validate .env, azd authentication/context, azure.yaml, and Bicep.
   azure-up           Build locally, preview infrastructure, then provision and deploy.
   azure-import       Import the public versioned Docker Hub image into ACR and deploy it.
   azure-push         Build and push the application image without updating Container Apps.
   azure-deploy       Build and deploy the application to existing Azure resources.
   azure-down         Confirm resource-group deletion, submit it, and return without waiting.

   Set AZURE_CUSTOM_DOMAIN in .env for an Azure-managed certificate.
   Set AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=CNAME (default) or HTTP for an apex domain.
   Deployment prints required DNS records; configure DNS and rerun azure-deploy if needed.


7) Misc

   help               Show this help.

Options
  -InstallMissing    Permit setup/check actions to install missing supported tooling.
  -SkipContainerCheck  Skip container-engine inspection for isolated CI execution only.
  -ContainerEngine   Local container engine: wslc (default) or docker.
  -Configuration     Build configuration; default: Release.
  -ImageName         Container image; default: mockapi:dev.
  -ContainerName     Container name; default: mockapi-dev.
  -VolumeName        Persistent /data volume; default: mockapi-data.
  -NuGetSource       NuGet HTTPS source for container restore; default: first enabled host source.
  -Port              Host port; default: 8080.
  -EnvironmentFile   Local Azure settings file; default: .env.
"@
}

function Show-SubmenuHelp {
  param(
    [Parameter(Mandatory)]
    [ValidateSet('Run locally', 'Verify', 'Azure', 'Containers', 'Setup')]
    [string] $Section
  )

  switch ($Section) {
    'Run locally' {
      Write-Host @"
MockAPI Developer CLI

 Run locally help

  [1] Run without tutorial
      Launch with local .NET and open the dashboard without the first-use tour.
  [2] Run with tutorial
      Launch with local .NET and open the first-use tour, even if previously dismissed.

  The launch choice does not reset saved dashboard preferences.
  Use run or run-tutorial for the equivalent command-line actions.
"@
    }
    'Verify' {
      Write-Host @"
MockAPI Developer CLI

 Verify help

  [1] Validate managed code
       Restore, scan dependencies, lint, build, test, collect coverage, and publish.
  [2] Run unit tests
       Run the repository test suites.
"@
    }
    'Azure' {
      Write-Host @"
MockAPI Developer CLI

 Azure help

  Set AZURE_CUSTOM_DOMAIN in .env for an Azure-managed certificate.
  Set AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=CNAME (default) or HTTP for an apex domain.
  Deployment prints required DNS records; configure DNS and rerun azure-deploy if needed.

  [1] Test, build, and deploy initial Azure environment
       Run tests and build before provisioning and deploying.
  [2] Test, build, and update existing Azure deployment
       Run tests and build before updating an existing deployment.
  [3] Set up Azure Tooling
       Verify Azure tooling and optionally install or update it.
  [4] Validate deployment configuration
       Validate local settings, authentication, azd configuration, and Bicep.
  [5] Provision and deploy
       Build locally, preview infrastructure, then provision and deploy.
  [6] Build, push, and deploy image
       Build and deploy the application to existing Azure resources.
  [7] Build and push image only
       Build and push the application image without updating Container Apps.
  [8] Import Docker Hub image and deploy
       Import the public versioned image into ACR and deploy it.
  [9] Delete Azure resources
       Delete the azd environment resources with confirmation.
"@
    }
    'Containers' {
      Write-Host @"
MockAPI Developer CLI

 Containers help

  [1] Use WSLC
       Use WSLC for local container actions.
  [2] Use Docker
       Use Docker for local container actions.
  [3] Build Dockerfile ($ContainerEngine)
       Build a uniquely tagged image and update the local image alias.
  [4] Start or restart native container ($ContainerEngine)
       Create or start the development container.
  [5] Test running container
       Send an HTTP smoke test to the running container.
  [6] Load and showcase built-in example
       Load examples and verify rate limiting and statistics.
  [7] Show container logs
       Show recent container log lines.
  [8] Show container status
       Inspect the container.
  [9] Stop container
       Stop the container with a one-second graceful shutdown window.
  [10] Remove container
       Remove the stopped container while retaining its data volume.
"@
    }
    'Setup' {
      Write-Host @"
MockAPI Developer CLI

 Setup help

  [1] Setup local dependencies
       Check prerequisites and restore development dependencies.
  [2] Update dependencies and pnpm meeting the cooldown
       Update eligible dependencies first, then update the pnpm pin.
"@
    }
  }
}

function Show-Menu {
  param(
    [ValidateSet('Home', 'Run locally', 'Verify', 'Azure', 'Containers', 'Setup')]
    [string] $InitialMenu = 'Home'
  )

  $containerEngineVariable = Get-Variable -Name ContainerEngine -ErrorAction SilentlyContinue
  $currentContainerEngine = if ($null -eq $containerEngineVariable) { 'wslc' } else { [string] $containerEngineVariable.Value }
  $sections = [ordered]@{
    'Home' = [ordered]@{
      'l' = @{ Label = 'Run locally' }
      'v' = @{ Label = 'Verify' }
      'a' = @{ Label = 'Azure' }
      'c' = @{ Label = 'Containers' }
      's' = @{ Label = 'Setup' }
      'h' = @{ Label = 'Help'; Action = 'help' }
      'q' = @{ Label = 'Quit'; Action = 'quit' }
    }
    'Run locally' = [ordered]@{
      '1' = @{ Label = 'Run without tutorial'; Action = 'run' }
      '2' = @{ Label = 'Run with tutorial'; Action = 'run-tutorial' }
    }
    'Setup' = [ordered]@{
      '1' = @{ Label = 'Setup local dependencies'; Action = 'setup' }
      '2' = @{ Label = 'Update dependencies and pnpm meeting the cooldown'; Action = 'dependency-tooling-update' }
    }
    'Verify' = [ordered]@{
      '1' = @{ Label = 'Validate managed code'; Action = 'validate' }
      '2' = @{ Label = 'Run unit tests'; Action = 'test' }
    }
    'Containers' = [ordered]@{
      '1' = @{ Label = 'Use WSLC'; Action = 'container-engine-wslc' }
      '2' = @{ Label = 'Use Docker'; Action = 'container-engine-docker' }
      '3' = @{ Label = "Build Dockerfile ($currentContainerEngine)"; Action = 'container-build' }
      '4' = @{ Label = "Start or restart native container ($currentContainerEngine)"; Action = 'container-run' }
      '5' = @{ Label = 'Test running container'; Action = 'container-test' }
      '6' = @{ Label = 'Load and showcase built-in example'; Action = 'container-showcase' }
      '7' = @{ Label = 'Show container logs'; Action = 'container-logs' }
      '8' = @{ Label = 'Show container status'; Action = 'container-status' }
      '9' = @{ Label = 'Stop container'; Action = 'container-stop' }
      '10' = @{ Label = 'Remove container'; Action = 'container-remove' }
    }
    'Azure' = [ordered]@{
      '1' = @{ Label = 'Test, build, and deploy initial Azure environment'; Action = 'pathway-azure-initial' }
      '2' = @{ Label = 'Test, build, and update existing Azure deployment'; Action = 'pathway-azure-update' }
      '3' = @{ Label = 'Set up Azure Tooling'; Action = 'azure-setup' }
      '4' = @{ Label = 'Validate deployment configuration'; Action = 'azure-check' }
      '5' = @{ Label = 'Provision and deploy'; Action = 'azure-up' }
      '6' = @{ Label = 'Build, push, and deploy image'; Action = 'azure-deploy' }
      '7' = @{ Label = 'Build and push image only'; Action = 'azure-push' }
      '8' = @{ Label = 'Import Docker Hub image and deploy'; Action = 'azure-import' }
      '9' = @{ Label = 'Delete Azure resources'; Action = 'azure-down' }
    }
  }

  $menuGroups = @{
    'l' = 'Run locally'
    'v' = 'Verify'
    'a' = 'Azure'
    'c' = 'Containers'
    's' = 'Setup'
  }
  $choices = @{
    'h' = @{ Action = 'help' }
    'q' = @{ Action = 'quit' }
    'p1' = @{ Action = 'pathway-azure-initial' }
    'p2' = @{ Action = 'pathway-azure-update' }
    'a1' = @{ Action = 'azure-setup' }
    'a2' = @{ Action = 'azure-check' }
    'a3' = @{ Action = 'azure-up' }
    'a4' = @{ Action = 'azure-deploy' }
    'a5' = @{ Action = 'azure-push' }
    'a6' = @{ Action = 'azure-import' }
    'a7' = @{ Action = 'azure-down' }
    'c1' = @{ Action = 'container-engine-wslc' }
    'c2' = @{ Action = 'container-engine-docker' }
    'c3' = @{ Action = 'container-build' }
    'c4' = @{ Action = 'container-run' }
    'c5' = @{ Action = 'container-test' }
    'c6' = @{ Action = 'container-showcase' }
    'c7' = @{ Action = 'container-logs' }
    'c8' = @{ Action = 'container-status' }
    'c9' = @{ Action = 'container-stop' }
    'c10' = @{ Action = 'container-remove' }
    's1' = @{ Action = 'setup' }
    's2' = @{ Action = 'dependency-tooling-update' }
    'v1' = @{ Action = 'validate' }
    'v2' = @{ Action = 'test' }
  }
  $choices['s3'] = @{ Action = 'pnpm-update' }
  $choices['l1'] = @{ Action = 'run' }
  $choiceWidth = ($choices.Keys | ForEach-Object { "[$_]".Length } | Measure-Object -Maximum).Maximum
  $currentMenu = $InitialMenu

  while ($true) {
    Reset-ConsoleColors
    Write-Host ''
    Write-Host 'MockAPI Developer CLI' -ForegroundColor Cyan
    Write-Host ('=' * 21) -ForegroundColor Cyan
    Write-Host ''
    Write-Host $currentMenu -ForegroundColor Yellow
    Write-Host ''
    foreach ($key in $sections[$currentMenu].Keys) {
      if ($currentMenu -eq 'Home' -and $key -eq 'h') {
        Write-Host ''
      }
      $formattedChoice = "[$key]".PadRight($choiceWidth)
      Write-Host "   $formattedChoice  $($sections[$currentMenu][$key].Label)"
    }
    if ($currentMenu -ne 'Home') {
      Write-Host ''
      foreach ($entry in @(@('b', 'Back'), @('h', 'Help'), @('q', 'Quit'))) {
        $formattedChoice = "[$($entry[0])]".PadRight($choiceWidth)
        Write-Host "   $formattedChoice  $($entry[1])"
      }
    }
    Write-Host ''
    Write-Host 'Action shortcuts work from any menu.'
    Write-Host ''
    $selection = (Read-Host 'Select an action').ToLowerInvariant()
    Write-Host ''
    $script:MenuContext = $currentMenu
    if ($menuGroups.ContainsKey($selection)) {
      $currentMenu = $menuGroups[$selection]
      continue
    }
    if ($selection -eq 'b' -and $currentMenu -ne 'Home') {
      $currentMenu = 'Home'
      continue
    }
    if ($selection -eq 'h' -and $currentMenu -ne 'Home') {
      return "help-$($currentMenu.ToLowerInvariant().Replace(' ', '-'))"
    }
    if ($currentMenu -ne 'Home' -and $sections[$currentMenu].Contains($selection)) {
      return [string] $sections[$currentMenu][$selection].Action
    }
    if ($choices.Contains($selection)) {
      return [string] $choices[$selection].Action
    }
    Write-Host "Unknown menu selection '$selection'." -ForegroundColor Red
  }
}

function Wait-ForMenuReturn {
  if ($script:DomainContinuationPrompted) {
    $script:DomainContinuationPrompted = $false
    return
  }
  Write-Host ''
  Write-Host ''
  Write-Host ('=' * 42) -ForegroundColor DarkCyan
  Write-Host 'Press any key to return to the menu.' -ForegroundColor Cyan
  Write-Host ('=' * 42) -ForegroundColor DarkCyan

  if ([Console]::IsInputRedirected) {
    $null = Read-Host
  }
  else {
    $null = [Console]::ReadKey($true)
  }
}

function Invoke-Action {
  param([Parameter(Mandatory)][string] $SelectedAction)

  switch ($SelectedAction) {
    'help' { Show-Help }
    'help-run-locally' { Show-SubmenuHelp -Section 'Run locally' }
    'help-verify' { Show-SubmenuHelp -Section Verify }
    'help-azure' { Show-SubmenuHelp -Section Azure }
    'help-containers' { Show-SubmenuHelp -Section Containers }
    'help-setup' { Show-SubmenuHelp -Section Setup }
    'check' { Test-Prerequisites }
    'setup' { Invoke-Setup }
    'dependencies-update' { Invoke-DependencyUpdates }
    'pnpm-update' { Invoke-PnpmUpdate }
    'dependency-tooling-update' { Invoke-DependencyToolingUpdates }
    'restore' { Invoke-Restore }
    'format' { Invoke-Format }
    'lint' { Invoke-Lint }
    'build' { Invoke-Build }
    'run' { Invoke-Run }
    'run-tutorial' { Invoke-Run -ShowTutorial }
    'test' { Invoke-Tests }
    'coverage' { Invoke-Coverage }
    'publish' { Invoke-Publish }
    'validate' { Invoke-Validation }
    'container-engine-wslc' { $script:ContainerEngine = 'wslc'; Write-Field 'Container engine' 'WSLC' Green }
    'container-engine-docker' { $script:ContainerEngine = 'docker'; Write-Field 'Container engine' 'DOCKER' Green }
    'container-build' { Invoke-ContainerBuild }
    'container-run' { Invoke-ContainerRun }
    'container-test' { Invoke-ContainerTest }
    'container-showcase' { Invoke-ContainerShowcase }
    'container-logs' { Invoke-ContainerLogs }
    'container-status' { Invoke-ContainerStatus }
    'container-stop' { Invoke-ContainerStop }
    'container-remove' { Invoke-ContainerRemove }
    'azure-setup' { Invoke-AzureSetup }
    'azure-check' { Invoke-AzureCheck | Out-Null }
    'azure-up' { Invoke-AzureUp }
    'azure-import' { Invoke-AzureImport }
    'azure-push' { Invoke-AzurePush }
    'azure-deploy' { Invoke-AzureDeploy }
    'azure-down' { Invoke-AzureDown }
    'pathway-azure-initial' { Invoke-InitialAzurePathway }
    'pathway-azure-update' { Invoke-UpdateAzurePathway }
    'all' { Invoke-All }
    default { throw "Unknown action '$SelectedAction'." }
  }
}

Push-Location $repositoryRoot
try {
  if ($Action -ne 'menu') {
    Invoke-Action -SelectedAction $Action
    return
  }

  $menuContext = 'Home'
  Clear-Host
  while ($true) {
    $selectedAction = Show-Menu -InitialMenu $menuContext
    $menuContext = $script:MenuContext
    if ($selectedAction -eq 'quit') {
      return
    }

    try {
      Invoke-Action -SelectedAction $selectedAction
    }
    catch {
      Write-Host ''
      Write-Host "Action failed: $($_.Exception.Message)" -ForegroundColor Red
    }

    Wait-ForMenuReturn
  }
}
finally {
  Reset-ConsoleColors
  Pop-Location
  [Console]::OutputEncoding = $originalConsoleOutputEncoding
}
