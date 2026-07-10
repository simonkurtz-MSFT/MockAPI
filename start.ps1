#!/usr/bin/env pwsh
<#
.SYNOPSIS
    MockAPI Developer CLI.

.DESCRIPTION
    Checks and prepares the local toolchain, restores packages, builds and tests
    the solution, collects coverage, publishes both Linux runtime targets, and
    manages the native development container through WSLC.

.EXAMPLE
    .\start.ps1
    .\start.ps1 -Action setup
    .\start.ps1 -Action setup -InstallMissing
    .\start.ps1 -Action coverage
    .\start.ps1 -Action container-build
    .\start.ps1 -Action container-run
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [ValidateSet(
    'menu', 'help', 'check', 'setup', 'restore', 'build', 'run', 'test', 'coverage',
    'publish', 'validate', 'container-build', 'container-run', 'container-test',
    'container-logs', 'container-status', 'container-stop', 'container-remove', 'all')]
  [string] $Action = 'menu',

  [switch] $InstallMissing,

  [ValidateNotNullOrEmpty()]
  [string] $Configuration = 'Release',

  [ValidateNotNullOrEmpty()]
  [string] $ImageName = 'mockapi:dev',

  [ValidateNotNullOrEmpty()]
  [string] $ContainerName = 'mockapi-dev',

  [ValidateNotNullOrEmpty()]
  [string] $VolumeName = 'mockapi-data',

  [ValidateRange(1, 65535)]
  [int] $Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = $PSScriptRoot
$solutionFile = Join-Path $repositoryRoot 'MockAPI.slnx'
$projectFile = Join-Path $repositoryRoot 'src/MockAPI/MockAPI.csproj'
$globalJsonFile = Join-Path $repositoryRoot 'global.json'
$dockerfile = Join-Path $repositoryRoot 'Dockerfile'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$coverageDirectory = Join-Path $artifactsDirectory 'coverage'
$publishDirectory = Join-Path $artifactsDirectory 'publish'
$fieldWidth = 20

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

function Invoke-Tool {
  param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string[]] $Arguments,
    [Parameter(Mandatory)][string] $Operation
  )

  Write-Host ''
  Write-Host $Operation -ForegroundColor Cyan
  & $Executable @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$Operation failed with exit code $LASTEXITCODE."
  }
}

function Get-PinnedSdkVersion {
  if (-not (Test-Path -LiteralPath $globalJsonFile -PathType Leaf)) {
    throw "SDK configuration was not found: $globalJsonFile"
  }

  $configuration = Get-Content -LiteralPath $globalJsonFile -Raw | ConvertFrom-Json
  return [string] $configuration.sdk.version
}

function Install-DotNetSdk {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "The .NET 10 SDK is missing and winget is unavailable. Install the pinned SDK from https://dotnet.microsoft.com/download/dotnet/10.0."
  }

  if ($PSCmdlet.ShouldProcess('Microsoft.DotNet.SDK.10', 'Install with winget')) {
    Invoke-Tool -Executable 'winget' -Operation 'Installing .NET 10 SDK' -Arguments @(
      'install', '--id', 'Microsoft.DotNet.SDK.10', '--exact',
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

  $pinnedVersion = Get-PinnedSdkVersion
  $resolvedVersion = (& dotnet --version).Trim()
  if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the .NET SDK version.'
  }
  if ($resolvedVersion -ne $pinnedVersion) {
    if ($InstallMissing) {
      Install-DotNetSdk
      $resolvedVersion = (& dotnet --version).Trim()
    }
    if ($resolvedVersion -ne $pinnedVersion) {
      throw "Repository requires .NET SDK $pinnedVersion, but this directory resolves $resolvedVersion."
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

  $versionOutput = (& wslc version | Select-Object -First 1).Trim()
  if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the WSLC version.'
  }
  Write-Field 'WSLC' $versionOutput Green
}

function Test-Prerequisites {
  Assert-DotNet
  Assert-Wslc
  Write-Field 'Solution' $solutionFile
  Write-Field 'Container engine' 'WSLC (native host architecture)'
  Write-Host ''
  Write-Host 'Prerequisite checks passed.' -ForegroundColor Green
}

function Invoke-Restore {
  Assert-DotNet
  Invoke-Tool -Executable 'dotnet' -Operation 'Restoring NuGet packages' -Arguments @(
    'restore', $solutionFile
  )
}

function Invoke-Build {
  Assert-DotNet
  Invoke-Tool -Executable 'dotnet' -Operation "Building solution ($Configuration)" -Arguments @(
    'build', $solutionFile, '--configuration', $Configuration,
    '-p:TreatWarningsAsErrors=true'
  )
}

function Invoke-Run {
  Assert-DotNet
  Invoke-Tool -Executable 'dotnet' -Operation 'Running MockAPI' -Arguments @(
    'run', '--project', $projectFile, '--configuration', $Configuration
  )
}

function Invoke-Tests {
  Assert-DotNet
  Invoke-Tool -Executable 'dotnet' -Operation "Running tests ($Configuration)" -Arguments @(
    'test', $solutionFile, '--configuration', $Configuration,
    '-p:TreatWarningsAsErrors=true'
  )
}

function Invoke-Coverage {
  Assert-DotNet
  New-Item -ItemType Directory -Path $coverageDirectory -Force | Out-Null
  Invoke-Tool -Executable 'dotnet' -Operation 'Running tests with XPlat code coverage' -Arguments @(
    'test', $solutionFile, '--configuration', $Configuration,
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
}

function Invoke-Publish {
  Assert-DotNet
  foreach ($runtime in @('linux-x64', 'linux-arm64')) {
    $outputDirectory = Join-Path $publishDirectory $runtime
    Invoke-Tool -Executable 'dotnet' -Operation "Publishing $runtime ($Configuration)" -Arguments @(
      'publish', $projectFile, '--configuration', $Configuration, '--runtime', $runtime,
      '--output', $outputDirectory, '-p:TreatWarningsAsErrors=true'
    )
  }
}

function Assert-ContainerInputs {
  Assert-Wslc
  if (-not (Test-Path -LiteralPath $dockerfile -PathType Leaf)) {
    throw "Container definition was not found: $dockerfile"
  }
}

function Get-WslcContainer {
  $output = @(& wslc inspect $ContainerName 2>$null)
  if ($LASTEXITCODE -ne 0) {
    return $null
  }

  $containers = @($output -join [Environment]::NewLine | ConvertFrom-Json)
  if ($containers.Count -eq 0) {
    return $null
  }
  return $containers[0]
}

function Test-WslcVolumeExists {
  & wslc volume inspect $VolumeName *> $null
  return $LASTEXITCODE -eq 0
}

function Invoke-ContainerBuild {
  Assert-ContainerInputs
  Invoke-Tool -Executable 'wslc' -Operation "Building native container image $ImageName" -Arguments @(
    'build', '--pull', '--tag', $ImageName, $repositoryRoot
  )
}

function Invoke-ContainerRun {
  Assert-ContainerInputs
  $container = Get-WslcContainer
  if ($null -ne $container) {
    if ([bool] $container.State.Running) {
      Write-Field 'Container' "$ContainerName is already running" Green
      Write-Field 'Application URL' "http://localhost:$Port" Green
      return
    }

    Invoke-Tool -Executable 'wslc' -Operation "Restarting container $ContainerName" -Arguments @(
      'start', $ContainerName
    )
    Write-Field 'Application URL' "http://localhost:$Port" Green
    return
  }
  if (-not (Test-WslcVolumeExists)) {
    Invoke-Tool -Executable 'wslc' -Operation "Creating volume $VolumeName" -Arguments @(
      'volume', 'create', $VolumeName
    )
  }

  Invoke-Tool -Executable 'wslc' -Operation "Starting container $ContainerName" -Arguments @(
    'run', '--detach', '--name', $ContainerName,
    '--cpus', '0.5', '--memory', '256M',
    '--publish', "${Port}:8080",
    '--volume', "${VolumeName}:/data",
    $ImageName
  )
  Write-Field 'Application URL' "http://localhost:$Port" Green
}

function Invoke-ContainerTest {
  Assert-Wslc
  $container = Get-WslcContainer
  if ($null -eq $container) {
    throw "Container '$ContainerName' does not exist. Run the container first."
  }
  if (-not [bool] $container.State.Running) {
    throw "Container '$ContainerName' is stopped (status: $($container.State.Status), exit code: $($container.State.ExitCode)). Run '.\start.ps1 -Action container-run' to restart it."
  }

  $uri = "http://localhost:$Port/"
  $attemptCount = 10
  $response = $null
  $lastFailure = $null
  for ($attempt = 1; $attempt -le $attemptCount; $attempt++) {
    try {
      $response = Invoke-WebRequest -Uri $uri -TimeoutSec 2
      break
    }
    catch {
      $lastFailure = $_.Exception.Message
      $container = Get-WslcContainer
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
    throw "Container smoke test expected HTTP 200 but received $($response.StatusCode)."
  }
  Write-Field 'Smoke test' "$uri -> HTTP $($response.StatusCode)" Green
}

function Invoke-ContainerLogs {
  Assert-Wslc
  Invoke-Tool -Executable 'wslc' -Operation "Showing logs for $ContainerName" -Arguments @(
    'logs', '--tail', '200', $ContainerName
  )
}

function Invoke-ContainerStatus {
  Assert-Wslc
  Invoke-Tool -Executable 'wslc' -Operation "Inspecting $ContainerName" -Arguments @(
    'inspect', $ContainerName
  )
}

function Invoke-ContainerStop {
  Assert-Wslc
  Invoke-Tool -Executable 'wslc' -Operation "Stopping $ContainerName" -Arguments @(
    'stop', $ContainerName
  )
}

function Invoke-ContainerRemove {
  Assert-Wslc
  if ($PSCmdlet.ShouldProcess($ContainerName, 'Remove WSLC container')) {
    Invoke-Tool -Executable 'wslc' -Operation "Removing $ContainerName" -Arguments @(
      'remove', $ContainerName
    )
  }
}

function Invoke-Setup {
  Test-Prerequisites
  Invoke-Restore
  Write-Host ''
  Write-Host 'Local development setup is ready.' -ForegroundColor Green
}

function Invoke-Validation {
  Invoke-Restore
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

function Show-Help {
  Write-Host @"
MockAPI Developer CLI

Usage:
  .\start.ps1 -Action <action> [options]

Setup and managed code:
  check              Verify the pinned .NET SDK and WSLC.
  setup              Check prerequisites and restore NuGet packages.
  restore            Restore NuGet packages.
  build              Build with warnings treated as errors.
  run                Run MockAPI directly with dotnet.
  test               Run all tests.
  coverage           Run tests and write Cobertura output under artifacts/coverage.
  publish            Publish trimmed linux-x64 and linux-arm64 artifacts.
  validate           Restore, build, test, collect coverage, and publish.

Native container workflow (WSLC):
  container-build    Build $ImageName from the Dockerfile.
  container-run      Create, start, or restart $ContainerName on port $Port.
  container-test     Send an HTTP smoke test to the running container.
  container-logs     Show the last 200 container log lines.
  container-status   Inspect the container.
  container-stop     Stop the container.
  container-remove   Remove the stopped container; the data volume is retained.
  all                Run managed validation and build the native container image.

Options:
  -InstallMissing    Permit setup/check to install .NET 10 with winget or update WSL.
  -Configuration     Build configuration; default: Release.
  -ImageName         Container image; default: mockapi:dev.
  -ContainerName     Container name; default: mockapi-dev.
  -VolumeName        Persistent /data volume; default: mockapi-data.
  -Port              Host port; default: 8080.
"@
}

function Show-Menu {
  $choices = [ordered]@{
    '1' = @{ Label = 'Setup local dependencies'; Action = 'setup' }
    '2' = @{ Label = 'Validate managed code'; Action = 'validate' }
    '3' = @{ Label = 'Build native container'; Action = 'container-build' }
    '4' = @{ Label = 'Start or restart native container'; Action = 'container-run' }
    '5' = @{ Label = 'Test running container'; Action = 'container-test' }
    '6' = @{ Label = 'Show container logs'; Action = 'container-logs' }
    '7' = @{ Label = 'Stop container'; Action = 'container-stop' }
    '8' = @{ Label = 'Remove container'; Action = 'container-remove' }
    '9' = @{ Label = 'Run all local validation'; Action = 'all' }
    'h' = @{ Label = 'Help'; Action = 'help' }
    'q' = @{ Label = 'Quit'; Action = 'quit' }
  }

  while ($true) {
    Write-Host ''
    Write-Host 'MockAPI Developer CLI' -ForegroundColor Cyan
    foreach ($key in $choices.Keys) {
      Write-Host "  $key) $($choices[$key].Label)"
    }
    Write-Host ''
    $selection = (Read-Host 'Select an action').ToLowerInvariant()
    if ($choices.Contains($selection)) {
      return [string] $choices[$selection].Action
    }
    Write-Host "Unknown menu selection '$selection'." -ForegroundColor Red
  }
}

function Invoke-Action {
  param([Parameter(Mandatory)][string] $SelectedAction)

  switch ($SelectedAction) {
    'help' { Show-Help }
    'check' { Test-Prerequisites }
    'setup' { Invoke-Setup }
    'restore' { Invoke-Restore }
    'build' { Invoke-Build }
    'run' { Invoke-Run }
    'test' { Invoke-Tests }
    'coverage' { Invoke-Coverage }
    'publish' { Invoke-Publish }
    'validate' { Invoke-Validation }
    'container-build' { Invoke-ContainerBuild }
    'container-run' { Invoke-ContainerRun }
    'container-test' { Invoke-ContainerTest }
    'container-logs' { Invoke-ContainerLogs }
    'container-status' { Invoke-ContainerStatus }
    'container-stop' { Invoke-ContainerStop }
    'container-remove' { Invoke-ContainerRemove }
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

  while ($true) {
    $selectedAction = Show-Menu
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
  }
}
finally {
  Pop-Location
}