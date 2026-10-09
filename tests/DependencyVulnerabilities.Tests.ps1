#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$auditScript = Join-Path $PSScriptRoot '../scripts/Test-DependencyVulnerabilities.ps1'

function Invoke-AuditTest {
  param(
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $PnpmReport,
    [int] $PnpmExitCode = 0,
    [string] $NuGetReport = '{"projects":[]}',
    [int] $NuGetExitCode = 0,
    [string] $ExpectedError,
    [string[]] $ExpectedOutput = @(),
    [bool] $ExpectNuGet = $true
  )

  $state = [pscustomobject]@{ NuGetCalled = $false }
  function Get-Command {
    param([string] $Name, [string] $ErrorAction)
    switch ($Name) {
      pnpm { [pscustomobject]@{ Source = 'Invoke-TestPnpm' } }
      dotnet { [pscustomobject]@{ Source = 'Invoke-TestNuGet' } }
      default { throw "Unexpected tool lookup: $Name" }
    }
  }
  function Invoke-TestPnpm {
    if (($args -join ' ') -ne 'audit --audit-level low --json') {
      throw 'pnpm must request a JSON audit including low-severity findings.'
    }
    $global:LASTEXITCODE = $PnpmExitCode
    $PnpmReport
  }
  function Invoke-TestNuGet {
    $state.NuGetCalled = $true
    if (($args -join ' ') -notmatch '--vulnerable --include-transitive --format json') {
      throw 'NuGet must audit both direct and transitive dependencies.'
    }
    $global:LASTEXITCODE = $NuGetExitCode
    $NuGetReport
  }

  $failure = $null
  $output = @()
  try {
    $output = @(& $auditScript -Solution 'test.slnx' 3>&1 6>&1)
  } catch {
    $failure = $_
  }
  if ($ExpectedError) {
    if ($null -eq $failure -or $failure.Exception.Message -notmatch $ExpectedError) {
      throw "${Name}: expected error '$ExpectedError', received '$failure'."
    }
  } elseif ($null -ne $failure) {
    throw "${Name}: unexpected failure: $failure"
  }
  $text = $output -join "`n"
  foreach ($expected in $ExpectedOutput) {
    if ($text -notmatch $expected) {
      throw "${Name}: missing output '$expected': $text"
    }
  }
  if (-not $ExpectedError -and $text -match 'warning-only policy' -and $text -match 'No known') {
    throw "${Name}: vulnerable dependencies must not produce a clean-scan message."
  }
  if ($state.NuGetCalled -ne $ExpectNuGet) {
    throw "${Name}: unexpected NuGet invocation state."
  }
}

$cleanPnpm = '{"metadata":{"vulnerabilities":{"info":0,"low":0,"moderate":0,"high":0,"critical":0}}}'
$vulnerablePnpm = '{"advisories":{"1":{"module_name":"braces","severity":"high","url":"https://github.com/advisories/GHSA-vfj7-8cjw-p6xm"}},"metadata":{"vulnerabilities":{"info":0,"low":0,"moderate":0,"high":1,"critical":0}}}'
$vulnerableNuGet = @{
  projects = @(@{
    path = 'test.csproj'
    frameworks = @(@{
      topLevelPackages = @(@{
        id = 'Direct.Package'; resolvedVersion = '1.0.0'
        vulnerabilities = @(@{ severity = 'High'; advisoryUrl = 'https://example.test/direct' })
      })
      transitivePackages = @(@{
        id = 'Transitive.Package'; resolvedVersion = '2.0.0'
        vulnerabilities = @(@{ severity = 'Low'; advisoryUrl = 'https://example.test/transitive' })
      })
    })
  })
} | ConvertTo-Json -Depth 10

Invoke-AuditTest -Name 'Clean audits succeed' -PnpmReport $cleanPnpm -ExpectedOutput @('No known pnpm or NuGet')
Invoke-AuditTest -Name 'pnpm findings warn and still scan NuGet' -PnpmReport $vulnerablePnpm -PnpmExitCode 1 -ExpectedOutput @(
  'pnpm reported 1 dependency vulnerabilities', 'GHSA-vfj7-8cjw-p6xm', 'completed with vulnerabilities'
)
Invoke-AuditTest -Name 'NuGet findings warn for direct and transitive packages' -PnpmReport $cleanPnpm -NuGetReport $vulnerableNuGet -ExpectedOutput @(
  'NuGet reported vulnerable dependencies', 'Direct.Package@1.0.0', 'Transitive.Package@2.0.0',
  'https://example.test/direct', 'https://example.test/transitive', 'completed with vulnerabilities'
)
Invoke-AuditTest -Name 'Both ecosystems report findings' -PnpmReport $vulnerablePnpm -PnpmExitCode 1 -NuGetReport $vulnerableNuGet -ExpectedOutput @(
  'pnpm reported 1 dependency vulnerabilities', 'NuGet reported vulnerable dependencies'
)
Invoke-AuditTest -Name 'pnpm operational failure stays fatal' -PnpmReport 'Registry unavailable' -PnpmExitCode 2 -ExpectedError 'pnpm vulnerability audit failed' -ExpectNuGet $false
Invoke-AuditTest -Name 'pnpm error report stays fatal' -PnpmReport '{"error":{"message":"Registry unavailable"}}' -PnpmExitCode 1 -ExpectedError 'invalid report' -ExpectNuGet $false
Invoke-AuditTest -Name 'pnpm failure without findings stays fatal' -PnpmReport $cleanPnpm -PnpmExitCode 1 -ExpectedError 'failed without reporting vulnerabilities' -ExpectNuGet $false
Invoke-AuditTest -Name 'Malformed pnpm JSON stays fatal' -PnpmReport '{' -ExpectedError 'JSON' -ExpectNuGet $false
Invoke-AuditTest -Name 'Invalid pnpm count stays fatal' -PnpmReport ($cleanPnpm.Replace('"high":0', '"high":-1')) -ExpectedError 'invalid count' -ExpectNuGet $false
Invoke-AuditTest -Name 'NuGet operational failure stays fatal' -PnpmReport $cleanPnpm -NuGetReport 'Feed unavailable' -NuGetExitCode 1 -ExpectedError 'NuGet vulnerability audit failed'
Invoke-AuditTest -Name 'Malformed NuGet JSON stays fatal' -PnpmReport $cleanPnpm -NuGetReport '{' -ExpectedError 'JSON'

Write-Host 'Dependency vulnerability policy tests passed (11 scenarios).'
