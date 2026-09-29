[CmdletBinding()]
param([string] $Solution = (Join-Path $PSScriptRoot '../MockAPI.slnx'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pnpm = Get-Command pnpm -ErrorAction Stop
& $pnpm.Source audit --audit-level low
if ($LASTEXITCODE -ne 0) {
  throw 'pnpm reported one or more dependency vulnerabilities.'
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
  throw "NuGet reported vulnerable dependencies:$([Environment]::NewLine)$($vulnerablePackages -join [Environment]::NewLine)"
}

Write-Host 'No known pnpm or NuGet dependency vulnerabilities were found.'
