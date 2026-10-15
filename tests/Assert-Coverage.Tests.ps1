#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$coverageScript = Join-Path $repositoryRoot 'scripts/Assert-Coverage.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "mockapi-coverage-policy-$([Guid]::NewGuid().ToString('N'))"

function Assert-True {
  param(
    [Parameter(Mandatory)][bool] $Condition,
    [Parameter(Mandatory)][string] $Message
  )

  if (-not $Condition) {
    throw $Message
  }
}

function New-CoverageReport {
  param(
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][double] $LineRate,
    [Parameter(Mandatory)][double] $BranchRate
  )

  $path = Join-Path $testRoot "$Name.xml"
  Set-Content -LiteralPath $path -Value "<coverage line-rate=`"$LineRate`" branch-rate=`"$BranchRate`" />"
  return $path
}

function Invoke-CoverageCheck {
  param([Parameter(Mandatory)][string] $Report)

  $standardOutput = Join-Path $testRoot "$([Guid]::NewGuid().ToString('N')).out"
  $standardError = Join-Path $testRoot "$([Guid]::NewGuid().ToString('N')).err"
  $process = Start-Process -FilePath (Get-Command pwsh).Source `
    -ArgumentList @('-NoProfile', '-File', $coverageScript, '-Report', $Report) `
    -RedirectStandardOutput $standardOutput `
    -RedirectStandardError $standardError `
    -Wait `
    -PassThru

  return [pscustomobject]@{
    ExitCode = $process.ExitCode
    Output = "$(Get-Content -LiteralPath $standardOutput -Raw)$(Get-Content -LiteralPath $standardError -Raw)"
  }
}

try {
  New-Item -ItemType Directory -Path $testRoot | Out-Null

  $complete = Invoke-CoverageCheck -Report (New-CoverageReport -Name 'complete' -LineRate 1 -BranchRate 1)
  Assert-True ($complete.ExitCode -eq 0) 'Complete backend coverage must pass.'
  Assert-True (-not $complete.Output.Contains('WARNING')) 'Complete backend coverage must not emit a warning.'

  $belowTarget = Invoke-CoverageCheck -Report (New-CoverageReport -Name 'below-target' -LineRate 1 -BranchRate 0.9991)
  Assert-True ($belowTarget.ExitCode -eq 0) 'Backend coverage above the checked-in floors must pass.'
  Assert-True ($belowTarget.Output.Contains('WARNING')) 'Backend coverage below 100% must emit a warning.'
  Assert-True ($belowTarget.Output.Contains('99.91% branches')) 'The warning must report the measured branch coverage.'

  $belowFloor = Invoke-CoverageCheck -Report (New-CoverageReport -Name 'below-floor' -LineRate 0.8999 -BranchRate 1)
  Assert-True ($belowFloor.ExitCode -ne 0) 'Backend coverage below a checked-in floor must fail.'
  Assert-True ($belowFloor.Output.Contains('at least 90% lines and 85% branches')) 'A floor failure must explain both thresholds.'

  Write-Host 'Backend coverage policy tests passed.'
}
finally {
  if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
  }
}
