[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $Report,
  [ValidateRange(0, 100)][double] $MinimumLines = 100,
  [ValidateRange(0, 100)][double] $MinimumBranches = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Report -PathType Leaf)) {
  throw "Coverage report was not found: $Report"
}

[xml] $coverage = Get-Content -LiteralPath $Report -Raw
$linePercent = [math]::Round(100 * [double] $coverage.coverage.'line-rate', 2)
$branchPercent = [math]::Round(100 * [double] $coverage.coverage.'branch-rate', 2)

Write-Host "Backend coverage: $linePercent% lines, $branchPercent% branches"
if ($linePercent -lt $MinimumLines -or $branchPercent -lt $MinimumBranches) {
  throw "Backend coverage must be at least $MinimumLines% lines and $MinimumBranches% branches."
}
