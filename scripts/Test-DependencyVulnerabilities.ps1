[CmdletBinding()]
param([string] $Solution = (Join-Path $PSScriptRoot '../MockAPI.slnx'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pnpm = Get-Command pnpm -ErrorAction Stop
$pnpmOutput = @(& $pnpm.Source audit --audit-level low --json 2>&1)
$pnpmExitCode = $LASTEXITCODE
if ($pnpmExitCode -notin @(0, 1)) {
  throw "pnpm vulnerability audit failed:$([Environment]::NewLine)$($pnpmOutput -join [Environment]::NewLine)"
}

$pnpmAudit = $pnpmOutput -join [Environment]::NewLine | ConvertFrom-Json
if ($pnpmAudit.PSObject.Properties['error'] -or -not $pnpmAudit.PSObject.Properties['metadata']) {
  throw "pnpm vulnerability audit returned an invalid report:$([Environment]::NewLine)$($pnpmOutput -join [Environment]::NewLine)"
}
$pnpmVulnerabilityCount = 0
foreach ($severity in @('info', 'low', 'moderate', 'high', 'critical')) {
  $count = $pnpmAudit.metadata.vulnerabilities.$severity
  if (($count -isnot [long] -and $count -isnot [int]) -or $count -lt 0) {
    throw "pnpm vulnerability audit returned an invalid count for '$severity'."
  }
  $pnpmVulnerabilityCount += $count
}
if ($pnpmExitCode -ne 0 -and $pnpmVulnerabilityCount -eq 0) {
  throw "pnpm vulnerability audit failed without reporting vulnerabilities:$([Environment]::NewLine)$($pnpmOutput -join [Environment]::NewLine)"
}
if ($pnpmVulnerabilityCount -ne 0) {
  Write-Host ($pnpmOutput -join [Environment]::NewLine)
  Write-Warning "pnpm reported $pnpmVulnerabilityCount dependency vulnerabilities. Continuing under the warning-only dependency policy."
}

$dotnet = Get-Command dotnet -ErrorAction Stop
$auditOutput = @(& $dotnet.Source list $Solution package --vulnerable --include-transitive --format json 2>&1)
if ($LASTEXITCODE -ne 0) {
  throw "NuGet vulnerability audit failed:$([Environment]::NewLine)$($auditOutput -join [Environment]::NewLine)"
}

$audit = $auditOutput -join [Environment]::NewLine | ConvertFrom-Json
$vulnerablePackages = [Collections.Generic.List[string]]::new()
foreach ($project in @($audit.projects)) {
  $frameworks = $project.PSObject.Properties['frameworks']
  if ($null -eq $frameworks) {
    continue
  }
  foreach ($framework in @($frameworks.Value)) {
    foreach ($collectionName in 'topLevelPackages', 'transitivePackages') {
      $collection = $framework.PSObject.Properties[$collectionName]
      if ($null -eq $collection) {
        continue
      }
      foreach ($package in @($collection.Value)) {
        $vulnerabilities = $package.PSObject.Properties['vulnerabilities']
        if ($null -eq $vulnerabilities -or @($vulnerabilities.Value).Count -eq 0) {
          continue
        }
        $resolvedVersion = $package.PSObject.Properties['resolvedVersion']
        $version = if ($null -eq $resolvedVersion) { 'unknown' } else { [string] $resolvedVersion.Value }
        $vulnerablePackages.Add("$($package.id)@$version in $($project.path)")
      }
    }
  }
}

if ($vulnerablePackages.Count -ne 0) {
  Write-Host ($auditOutput -join [Environment]::NewLine)
  Write-Warning "NuGet reported vulnerable dependencies:$([Environment]::NewLine)$($vulnerablePackages -join [Environment]::NewLine)"
}

if ($pnpmVulnerabilityCount -ne 0 -or $vulnerablePackages.Count -ne 0) {
  Write-Host 'Dependency audits completed with vulnerabilities (warning-only policy).'
} else {
  Write-Host 'No known pnpm or NuGet dependency vulnerabilities were found.'
}
