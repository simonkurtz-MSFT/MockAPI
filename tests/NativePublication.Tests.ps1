[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Assert-True {
  param([bool] $Condition, [string] $Message)
  if (-not $Condition) {
    throw $Message
  }
}

$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/container-release.yml') -Raw
Assert-True (([regex]::Matches($releaseWorkflow, 'uses: docker/build-push-action@')).Count -eq 1) 'Release images must be compiled only in the native matrix, not rebuilt for publication.'
Assert-True ($releaseWorkflow -notmatch 'setup-qemu-action') 'Native AOT release compilation must not depend on QEMU.'
Assert-True ($releaseWorkflow.Contains('docker save --output') -and $releaseWorkflow.Contains('docker load --input')) 'Publication must transfer the tested native images between jobs.'
Assert-True ($releaseWorkflow.Contains('imagetools create --dry-run') -and $releaseWorkflow.Contains('.config.digest')) 'Publication must verify the tested image identity and index before tagging a release.'
Assert-True ($releaseWorkflow.Contains('ENABLE_RELEASE_PUBLISHING') -and $releaseWorkflow.Contains('approve_publication')) 'Publication must require opt-in and explicit manual approval.'
Assert-True ($releaseWorkflow.Contains('environment: release')) 'Registry secrets and publication must use the release environment.'
Assert-True (([regex]::Matches($releaseWorkflow, 'ref: \$\{\{ needs\.preflight\.outputs\.commit \}\}')).Count -eq 2) 'Native build and publication must both check out the resolved tag commit.'
Assert-True ($releaseWorkflow.Contains('actions/workflows/quality.yml/runs?head_sha=')) 'Publication must require quality evidence for the exact release commit.'
Assert-True ($releaseWorkflow.Contains('ignore-unfixed: false')) 'Unfixed high and critical vulnerabilities must block publication.'
Assert-True ($releaseWorkflow.Contains('gh release create "$IMAGE_TAG" --verify-tag')) 'GitHub releases must use the existing validated tag.'
Assert-True ($releaseWorkflow.Contains('--prerelease --latest=false')) 'Prereleases must never become the latest stable GitHub release.'
Assert-True ($releaseWorkflow -notmatch '(?m)^  release:') 'GitHub release creation must not recursively trigger container publication.'
foreach ($workflowName in @('container-pr.yml', 'container-release.yml')) {
  $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot ".github/workflows/$workflowName") -Raw
  Assert-True ($workflow.Contains('Test-PublishedApplication.ps1 -VerifyPersistence')) 'Both image workflows must test persisted configuration after container recreation.'
  Assert-True ($workflow.Contains('Assert-PublishContents.ps1 -PublishDirectory ./published -NativeAot')) 'Both image workflows must reject managed runtime and debug artifacts.'
  Assert-True ($workflow.Contains('outputs: type=docker,oci-mediatypes=true')) 'Both image workflows must preserve OCI manifests.'
  Assert-True ($workflow.Contains('/protected-default)" = "401"')) 'Both native images must reject mock calls with their default settings.'
  Assert-True ($workflow.Contains('--env MockApi__RequireApiKey=false')) 'Unauthenticated response fixtures must opt out of API-key enforcement explicitly.'
}

$publicationFixture = Join-Path ([IO.Path]::GetTempPath()) "mockapi-publication-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $publicationFixture | Out-Null
try {
  $nativeExecutable = Join-Path $publicationFixture 'MockAPI'
  [IO.File]::WriteAllBytes($nativeExecutable, [byte[]] @(0x7f, 0x45, 0x4c, 0x46))
  $publishValidator = Join-Path $repositoryRoot 'scripts/Assert-PublishContents.ps1'
  & $publishValidator -PublishDirectory $publicationFixture -NativeAot
  foreach ($forbiddenFile in @('MockAPI.dbg', 'MockAPI.pdb', 'MockAPI.dll', 'libcoreclr.so', 'MockAPI.runtimeconfig.json', 'mockapi.json.security.json')) {
    $forbiddenPath = Join-Path $publicationFixture $forbiddenFile
    [IO.File]::WriteAllText($forbiddenPath, 'fixture')
    $rejected = $false
    try {
      & $publishValidator -PublishDirectory $publicationFixture -NativeAot
    }
    catch {
      $rejected = $_.Exception.Message -match 'artifacts entered'
    }
    Assert-True $rejected "Native publication must reject $forbiddenFile."
    Remove-Item -LiteralPath $forbiddenPath
  }
  [IO.File]::WriteAllText($nativeExecutable, 'not a native binary')
  $rejected = $false
  try {
    & $publishValidator -PublishDirectory $publicationFixture -NativeAot
  }
  catch {
    $rejected = $_.Exception.Message -match 'ELF header'
  }
  Assert-True $rejected 'Native publication must reject a non-ELF executable.'
}
finally {
  Remove-Item -LiteralPath $publicationFixture -Recurse -Force
}
Write-Host 'Native publication and release workflow contract tests passed.'
