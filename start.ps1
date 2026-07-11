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
    'menu', 'help', 'check', 'setup', 'restore', 'format', 'lint', 'build', 'run', 'test', 'coverage',
    'publish', 'validate', 'container-build', 'container-run', 'container-test',
    'container-showcase', 'container-logs', 'container-status', 'container-stop',
    'container-remove', 'all')]
  [string] $Action = 'menu',

  [switch] $InstallMissing,

  [switch] $SkipContainerCheck,

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
$containerImageIdLabel = 'mockapi.image-id'

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
  $result = Invoke-NativeTool -Executable $Executable -Arguments $Arguments -StreamOutput
  if ($result.ExitCode -ne 0) {
    throw "$Operation failed with exit code $($result.ExitCode)."
  }
}

function Invoke-NativeTool {
  param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string[]] $Arguments,
    [switch] $StreamOutput
  )

  $global:LASTEXITCODE = 0
  $output = if ($StreamOutput) {
    & $Executable @Arguments | Out-Host
    @()
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
  $versionResult = Invoke-NativeTool -Executable 'dotnet' -Arguments @('--version')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Unable to resolve the .NET SDK version.'
  }
  $resolvedVersion = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
  if ($resolvedVersion -ne $pinnedVersion) {
    if ($InstallMissing) {
      Install-DotNetSdk
      $versionResult = Invoke-NativeTool -Executable 'dotnet' -Arguments @('--version')
      if ($versionResult.ExitCode -ne 0) {
        throw 'Unable to resolve the .NET SDK version after installation.'
      }
      $resolvedVersion = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
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

  $versionResult = Invoke-NativeTool -Executable 'wslc' -Arguments @('version')
  if ($versionResult.ExitCode -ne 0) {
    throw 'Unable to resolve the WSLC version.'
  }
  $versionOutput = ([string] ($versionResult.Output | Select-Object -First 1)).Trim()
  Write-Field 'WSLC' $versionOutput Green
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
  Assert-Wslc
  Write-Field 'Solution' $solutionFile
  Write-Field 'Container engine' 'WSLC (native host architecture)'
  Write-Host ''
  Write-Host 'Prerequisite checks passed.' -ForegroundColor Green
}

function Invoke-ToolingRestore {
  Assert-Pnpm
  Invoke-Tool -Executable 'pnpm' -Operation 'Restoring formatting tools' -Arguments @(
    'install', '--frozen-lockfile'
  )
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
  foreach ($runtime in @('linux-musl-x64', 'linux-musl-arm64')) {
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
  $result = Invoke-NativeTool -Executable 'wslc' -Arguments @('inspect', $ContainerName) 2>$null
  if ($result.ExitCode -ne 0) {
    return $null
  }

  $containers = @($result.Output -join [Environment]::NewLine | ConvertFrom-Json)
  if ($containers.Count -eq 0) {
    return $null
  }
  return $containers[0]
}

function Get-WslcImageId {
  $result = Invoke-NativeTool -Executable 'wslc' -Arguments @('image', 'inspect', $ImageName) 2>$null
  if ($result.ExitCode -ne 0) {
    throw "Container image '$ImageName' does not exist. Run '.\start.ps1 -Action container-build'."
  }

  $images = @($result.Output -join [Environment]::NewLine | ConvertFrom-Json)
  if ($images.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string] $images[0].Id)) {
    throw "Container image '$ImageName' does not expose an image ID."
  }
  return ([string] $images[0].Id).Trim()
}

function Get-WslcContainerImageId {
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

function Add-BuildTagToCurrentImage {
  $result = Invoke-NativeTool -Executable 'wslc' -Arguments @('image', 'inspect', $ImageName) 2>$null
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
  Invoke-Tool -Executable 'wslc' -Operation "Preserving current image as $buildImageName" -Arguments @(
    'image', 'tag', $ImageName, $buildImageName
  )
}

function Test-WslcVolumeExists {
  $result = Invoke-NativeTool -Executable 'wslc' -Arguments @('volume', 'inspect', $VolumeName) 2>$null
  return $result.ExitCode -eq 0
}

function Initialize-WslcVolumePermissions {
  $maintenanceContainerName = "$ContainerName-volume-init-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
  try {
    Invoke-Tool -Executable 'wslc' -Operation "Initializing volume permissions for $VolumeName" -Arguments @(
      'run', '--name', $maintenanceContainerName,
      '--user', 'root', '--entrypoint', 'chown',
      '--volume', "${VolumeName}:/data",
      $ImageName, '1654:1654', '/data'
    )
  }
  finally {
    $maintenanceContainer = Invoke-NativeTool -Executable 'wslc' -Arguments @(
      'inspect', $maintenanceContainerName
    ) 2>$null
    if ($maintenanceContainer.ExitCode -eq 0) {
      $removeResult = Invoke-NativeTool -Executable 'wslc' -StreamOutput -Arguments @(
        'remove', '--force', $maintenanceContainerName
      )
      if ($removeResult.ExitCode -ne 0) {
        Write-Warning "Unable to remove maintenance container '$maintenanceContainerName'."
      }
    }
  }
}

function Invoke-ContainerBuild {
  Assert-ContainerInputs
  Add-BuildTagToCurrentImage
  $buildImageName = New-BuildImageName -Timestamp ([DateTimeOffset]::UtcNow)
  Invoke-Tool -Executable 'wslc' -Operation "Building native container image $buildImageName" -Arguments @(
    'build', '--pull', '--tag', $buildImageName, $repositoryRoot
  )
  Invoke-Tool -Executable 'wslc' -Operation "Moving image alias $ImageName to $buildImageName" -Arguments @(
    'image', 'tag', $buildImageName, $ImageName
  )
  Write-Field 'Build image' $buildImageName Green
  Write-Field 'Current alias' $ImageName Green
}

function Invoke-ContainerRun {
  Assert-ContainerInputs
  $currentImageId = Get-WslcImageId
  $container = Get-WslcContainer
  if ($null -ne $container) {
    $containerImageId = Get-WslcContainerImageId -Container $container
    if ([string]::IsNullOrWhiteSpace($containerImageId) -or
        -not $containerImageId.Equals($currentImageId, [StringComparison]::OrdinalIgnoreCase)) {
      $reason = if ([string]::IsNullOrWhiteSpace($containerImageId)) {
        'does not record its image ID'
      }
      else {
        "uses image $containerImageId instead of $currentImageId"
      }
      Write-Field 'Container' "$ContainerName $reason; recreating" Yellow
      Invoke-Tool -Executable 'wslc' -Operation "Removing stale container $ContainerName" -Arguments @(
        'remove', '--force', $ContainerName
      )
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

    Invoke-Tool -Executable 'wslc' -Operation "Restarting container $ContainerName" -Arguments @(
      'start', $ContainerName
    )
    Invoke-ContainerTest -SkipPrerequisiteCheck
    Write-Field 'Application URL' "http://localhost:$Port" Green
    return
  }
  if (-not (Test-WslcVolumeExists)) {
    Invoke-Tool -Executable 'wslc' -Operation "Creating volume $VolumeName" -Arguments @(
      'volume', 'create', $VolumeName
    )
  }
  Initialize-WslcVolumePermissions

  Write-Host ''
  Write-Host 'WSLC may warn that this WSL kernel cannot limit swap separately. The 256 MiB memory limit remains active.' -ForegroundColor DarkYellow
  Invoke-Tool -Executable 'wslc' -Operation "Starting container $ContainerName" -Arguments @(
    'run', '--detach', '--name', $ContainerName,
    '--label', "${containerImageIdLabel}=${currentImageId}",
    '--cpus', '0.5', '--memory', '256M',
    '--publish', "${Port}:8080",
    '--volume', "${VolumeName}:/data",
    $ImageName
  )
  Invoke-ContainerTest -SkipPrerequisiteCheck
  Write-Field 'Application URL' "http://localhost:$Port" Green
}

function Invoke-ContainerTest {
  param([switch] $SkipPrerequisiteCheck)

  if (-not $SkipPrerequisiteCheck) {
    Assert-Wslc
  }
  $container = Get-WslcContainer
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
    throw "Container smoke test expected readiness to return HTTP 200 but received $($response.StatusCode)."
  }
  if ($response.Content -notmatch '"status"\s*:\s*"ready"') {
    throw "Container smoke test received an unexpected readiness response body."
  }
  Write-Field 'Smoke test' "$uri -> HTTP $($response.StatusCode)" Green

  $dashboardUri = "http://localhost:$Port/"
  $dashboard = Invoke-WebRequest -Uri $dashboardUri -TimeoutSec 2 -SkipHttpErrorCheck
  if ($dashboard.StatusCode -ne 200 -or $dashboard.Content -notmatch '<title>MockAPI') {
    throw "Container smoke test expected the administrative dashboard at '$dashboardUri'."
  }
  Write-Field 'Dashboard' "$dashboardUri -> HTTP $($dashboard.StatusCode)" Green
}

function Invoke-ContainerShowcase {
  if (-not $SkipContainerCheck) {
    Assert-Wslc
    $container = Get-WslcContainer
    if ($null -eq $container -or -not [bool] $container.State.Running) {
      throw "Container '$ContainerName' is not running. Run '.\start.ps1 -Action container-run' first."
    }
  }

  $baseUri = "http://localhost:$Port"
  $statisticsUri = "$baseUri/__mockapi/api/statistics"
  $exampleUri = "$baseUri/ex/rate-limited"
  $endpointId = '7b2d425d-75f1-4ded-a74e-503374a7e99e'
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
  $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $exampleUri)
  $request.Version = [Version]::new(1, 1)
  $request.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
  $response = $null
  try {
    $response = $httpClient.Send($request)
    if ([int] $response.StatusCode -eq 404) {
      throw "The built-in example is not loaded. Use 'Load examples' in the dashboard at $baseUri, then rerun the showcase."
    }
    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $sourceValues = [string]::Join(',', @($response.Headers.GetValues('X-Mock-Source')))
    $retryAfter = [string]::Join(',', @($response.Headers.GetValues('Retry-After')))
    $contentType = [string] $response.Content.Headers.ContentType
    Add-ShowcaseCheck '429 status' ([int] $response.StatusCode -eq 429) "HTTP $([int] $response.StatusCode)"
    Add-ShowcaseCheck 'Reason phrase' ($response.ReasonPhrase -ceq 'Too Many Requests') ([string] $response.ReasonPhrase)
    Add-ShowcaseCheck 'Retry-After' ($retryAfter -eq '30') $retryAfter
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
    $request.Dispose()
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
  Add-ShowcaseCheck 'Aggregate statistics' (
    ([long] $after.totalRequests - [long] $before.totalRequests) -eq 4 -and
    ([long] $after.matchedRequests - [long] $before.matchedRequests) -eq 2 -and
    ([long] $after.unmatchedRequests - [long] $before.unmatchedRequests) -eq 2
  ) "total +$([long] $after.totalRequests - [long] $before.totalRequests), matched +$([long] $after.matchedRequests - [long] $before.matchedRequests), unmatched +$([long] $after.unmatchedRequests - [long] $before.unmatchedRequests)"
  Add-ShowcaseCheck 'Endpoint statistics' (($afterEndpointTotal - $beforeEndpointTotal) -eq 2) "endpoint +$($afterEndpointTotal - $beforeEndpointTotal)"

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
    'stop', '--time', '1', $ContainerName
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
  Invoke-ToolingRestore
  Invoke-Restore
  Write-Host ''
  Write-Host 'Local development setup is ready.' -ForegroundColor Green
}

function Invoke-Validation {
  Invoke-ToolingRestore
  Invoke-Restore
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

function Show-Help {
  Write-Host @"
MockAPI Developer CLI

Usage:
  .\start.ps1 -Action <action> [options]

Setup and managed code:
  check              Verify the pinned .NET SDK, pnpm, and WSLC.
  setup              Check prerequisites and restore development dependencies.
  restore            Restore NuGet packages.
  format             Format supported repository files and managed code.
  lint               Check formatting, Markdown, and managed code style.
  build              Build with warnings treated as errors.
  run                Run MockAPI directly with dotnet.
  test               Run all tests.
  coverage           Run tests and write Cobertura output under artifacts/coverage.
  publish            Publish trimmed linux-musl-x64 and linux-musl-arm64 artifacts.
  validate           Restore, build, test, collect coverage, and publish.

Native container workflow (WSLC):
  container-build    Build a uniquely tagged image and move the $ImageName alias to it.
  container-run      Create or start $ContainerName on port $Port; recreate it when $ImageName changed.
  container-test     Send an HTTP smoke test to the running container.
  container-showcase Exercise the loaded rate-limit example and verify response and statistics behavior.
  container-logs     Show the last 200 container log lines.
  container-status   Inspect the container.
  container-stop     Stop the container with a one-second graceful shutdown window.
  container-remove   Remove the stopped container; the data volume is retained.
  all                Run managed validation and build the native container image.

Options:
  -InstallMissing    Permit setup/check to install .NET 10 with winget or update WSL.
  -SkipContainerCheck  Skip WSLC/container inspection for isolated CI execution only.
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
    '6' = @{ Label = 'Showcase loaded example'; Action = 'container-showcase' }
    '7' = @{ Label = 'Show container logs'; Action = 'container-logs' }
    '8' = @{ Label = 'Stop container'; Action = 'container-stop' }
    '9' = @{ Label = 'Remove container'; Action = 'container-remove' }
    'a' = @{ Label = 'Run all local validation'; Action = 'all' }
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
    'format' { Invoke-Format }
    'lint' { Invoke-Lint }
    'build' { Invoke-Build }
    'run' { Invoke-Run }
    'test' { Invoke-Tests }
    'coverage' { Invoke-Coverage }
    'publish' { Invoke-Publish }
    'validate' { Invoke-Validation }
    'container-build' { Invoke-ContainerBuild }
    'container-run' { Invoke-ContainerRun }
    'container-test' { Invoke-ContainerTest }
    'container-showcase' { Invoke-ContainerShowcase }
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
