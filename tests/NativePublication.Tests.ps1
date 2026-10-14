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
Assert-True ($releaseWorkflow.Contains('imagetools create --dry-run') -and $releaseWorkflow.Contains('if [ "$digest" != "$expected_id" ]; then')) 'Publication must verify the tested manifest identity and index before tagging a release.'
Assert-True ($releaseWorkflow.Contains("jq --exit-status --raw-output '.config.digest'") -and $releaseWorkflow.Contains('"$expected_config_digest"')) 'Publication must compare the uploaded configuration digest with the saved OCI image.'
Assert-True ($releaseWorkflow.Contains('reference="$IMAGE_NAME@$digest"')) 'Publication must qualify pushed digests with the configured registry repository.'
Assert-True ($releaseWorkflow -notmatch "RepoDigests") 'Publication must not select an ambiguous local repository digest after pushing an image.'
Assert-True ($releaseWorkflow.Contains('- name: Remove temporary candidate tags') -and $releaseWorkflow.Contains('"https://hub.docker.com/v2/repositories/$IMAGE_NAME/tags/$candidate/"')) 'Publication must remove architecture candidate tags through the Docker Hub tag API.'
Assert-True ($releaseWorkflow.Contains("- name: Remove temporary candidate tags`n        if: always()")) 'Temporary candidate tags must be removed after successful and failed publication attempts.'
Assert-True ($releaseWorkflow.Contains("- name: Remove temporary candidate tags`n        if: always()`n        continue-on-error: true")) 'Temporary tag cleanup failures must not fail publication or prevent evidence attachment.'
Assert-True ($releaseWorkflow.Contains('::warning::Docker Hub returned HTTP $status')) 'Temporary tag deletion failures must remain visible as workflow warnings.'
Assert-True ($releaseWorkflow.Contains('workflow_call:') -and $releaseWorkflow.Contains('workflow_dispatch:')) 'Publication must support automatic version releases and manual recovery.'
Assert-True ($releaseWorkflow -notmatch 'ENABLE_RELEASE_PUBLISHING|approve_publication|environment: release') 'Publication must not retain a separate approval gate after a validated version change.'
Assert-True (([regex]::Matches($releaseWorkflow, 'ref: \$\{\{ needs\.preflight\.outputs\.commit \}\}')).Count -eq 2) 'Native build and publication must both check out the resolved tag commit.'
Assert-True ($releaseWorkflow -notmatch 'release-quality\.cjs|actions:\s+read') 'Manual recovery must not require a second full quality run after the immutable tag and release exist.'
Assert-True ($releaseWorkflow.Contains('ignore-unfixed: false')) 'Unfixed high and critical vulnerabilities must block publication.'
Assert-True ($releaseWorkflow.Contains('node scripts/release-notes.cjs verify "$IMAGE_TAG"')) 'Container publication must verify the existing release for the validated tag.'
Assert-True ($releaseWorkflow.IndexOf('node scripts/release-notes.cjs verify "$IMAGE_TAG"') -lt $releaseWorkflow.IndexOf('docker login')) 'The existing release must be verified before registry login or image publication.'
Assert-True ($releaseWorkflow.IndexOf('node scripts/release-notes.cjs verify "$IMAGE_TAG"') -lt $releaseWorkflow.IndexOf('  native-image:')) 'Missing or mismatched GitHub releases must fail before native builds.'
Assert-True ($releaseWorkflow.IndexOf('docker login') -lt $releaseWorkflow.IndexOf('  native-image:')) 'Registry credentials must be checked before native builds.'
Assert-True ($releaseWorkflow.Contains('if [ -z "$DOCKERHUB_USERNAME" ] || [ -z "$DOCKERHUB_TOKEN" ]; then')) 'Missing registry secrets must produce an actionable preflight error.'
Assert-True ($releaseWorkflow.Contains('docker manifest inspect "$IMAGE_NAME:$IMAGE_TAG"') -and $releaseWorkflow.IndexOf('docker manifest inspect "$IMAGE_NAME:$IMAGE_TAG"') -lt $releaseWorkflow.IndexOf('  native-image:')) 'Existing release images and registry lookup failures must be detected before native builds.'
Assert-True ($releaseWorkflow.Contains('docker buildx imagetools inspect "$IMAGE_NAME:$IMAGE_TAG"')) 'Publication must recheck tag absence after native builds to protect immutable releases.'
Assert-True ($releaseWorkflow.Contains('for tool in docker gh; do command -v "$tool"; done') -and $releaseWorkflow.Contains('docker info >/dev/null')) 'Preflight must verify its tools and Docker daemon before checking release prerequisites.'
Assert-True ($releaseWorkflow.Contains('ref: ${{ steps.version.outputs.commit }}')) 'Preflight release verification must check out the tagged source rather than the current branch tip.'
Assert-True ($releaseWorkflow.Contains('needs: preflight') -and $releaseWorkflow.Contains('needs: [preflight, native-image]')) 'Native builds and publication must remain gated by their prerequisites.'
Assert-True (([regex]::Matches($releaseWorkflow, 'continue-on-error')).Count -eq 1) 'Only temporary tag cleanup may continue on error; release prerequisites must remain fatal.'
Assert-True ($releaseWorkflow.Contains("        if: always()")) 'Native evidence must remain available when validation or scanning fails.'
Assert-True ($releaseWorkflow.Contains('gh release upload "$IMAGE_TAG" --repo "$GITHUB_REPOSITORY"')) 'Container publication must attach evidence to the existing GitHub release.'
Assert-True ($releaseWorkflow -notmatch 'gh release (create|edit)|release-notes\.cjs publish') 'GitHub release creation and notes must remain owned by the quality workflow.'
Assert-True ($releaseWorkflow -notmatch '--generate-notes|--clobber') 'Container publication must not replace reviewed release notes or existing evidence assets.'
Assert-True ($releaseWorkflow -notmatch '(?m)^  release:') 'GitHub release creation must not recursively trigger container publication.'
$qualityWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/quality.yml') -Raw
Assert-True ($qualityWorkflow.Contains('needs: version-tag') -and $qualityWorkflow.Contains("if: needs.version-tag.outputs.decision != 'unchanged'")) 'Only a new validated version may call container publication automatically.'
Assert-True ($qualityWorkflow.Contains('uses: ./.github/workflows/container-release.yml') -and $qualityWorkflow.Contains('tagname: ${{ needs.version-tag.outputs.tag }}')) 'The validated tag must be passed to the reusable container workflow.'
Assert-True ($qualityWorkflow.Contains('secrets: inherit')) 'Automatic publication must make configured Docker Hub credentials available to the reusable workflow.'
Assert-True ($qualityWorkflow.Contains('run: pwsh -NoProfile ./tests/NativePublication.Tests.ps1')) 'Quality validation must execute release workflow contract tests before tagging.'
$containerValidationWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/container-pr.yml') -Raw
Assert-True ($containerValidationWorkflow.Contains("on:`n  workflow_dispatch:`n  pull_request:")) 'Container validation must support a baseline-free manual rerun from the current branch.'
foreach ($workflowName in @('container-pr.yml', 'container-release.yml')) {
  $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot ".github/workflows/$workflowName") -Raw
  Assert-True ($workflow.Contains('Test-PublishedApplication.ps1 -VerifyPersistence')) 'Both image workflows must test persisted configuration after container recreation.'
  Assert-True ($workflow.Contains('Assert-PublishContents.ps1 -PublishDirectory ./published -NativeAot')) 'Both image workflows must reject managed runtime and debug artifacts.'
  Assert-True ($workflow.Contains('outputs: type=docker,oci-mediatypes=true')) 'Both image workflows must preserve OCI manifests.'
  Assert-True ($workflow.Contains('/protected-default)" = "401"')) 'Both native images must reject mock calls with their default settings.'
  Assert-True ($workflow.Contains('--env MockApi__RequireApiKey=false')) 'Unauthenticated response fixtures must opt out of API-key enforcement explicitly.'
  Assert-True (([regex]::Matches($workflow, 'curl --retry 20 --retry-all-errors')).Count -eq 2) 'Both container restarts must tolerate transient readiness connection errors.'
  Assert-True (([regex]::Matches($workflow, '--env MockApi__EnableOpenApi=true --env MockApi__EnableSwaggerUi=true')).Count -eq 3) 'Every native container instance must explicitly enable the surfaces required by publication probes.'
  Assert-True ($workflow.Contains('for tool in docker pwsh curl jq; do command -v "$tool"; done')) 'Native runner tools must be checked before image compilation.'
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
