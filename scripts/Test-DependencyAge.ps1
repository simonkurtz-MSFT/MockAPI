[CmdletBinding()]
param(
  [ValidateRange(1, 365)][int] $MinimumAgeDays = 7,
  [string] $ExceptionsFile = (Join-Path $PSScriptRoot '../.github/dependency-age-exceptions.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cutoff = [DateTimeOffset]::UtcNow.AddDays(-$MinimumAgeDays)
& (Join-Path $PSScriptRoot 'Test-WorkflowPins.ps1')
if (-not $?) {
  throw 'Workflow pin validation failed.'
}
$exceptions = if (Test-Path -LiteralPath $ExceptionsFile -PathType Leaf) {
  @((Get-Content -LiteralPath $ExceptionsFile -Raw | ConvertFrom-Json).exceptions)
}
else {
  @()
}
$violations = [Collections.Generic.List[string]]::new()
$verified = [Collections.Generic.List[object]]::new()

function Test-Exception {
  param(
    [Parameter(Mandatory)][string] $Ecosystem,
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $Version
  )

  $match = @($exceptions | Where-Object {
      $_.ecosystem -eq $Ecosystem -and $_.name -eq $Name -and $_.version -eq $Version
    } | Select-Object -First 1)
  if ($match.Count -eq 0) {
    return $false
  }
  $entry = $match[0]
  foreach ($property in 'reason', 'approver', 'evidence', 'expiresUtc') {
    if ([string]::IsNullOrWhiteSpace([string] $entry.$property)) {
      throw "Dependency age exception for ${Ecosystem}:${Name}@${Version} is missing '$property'."
    }
  }
  if ([DateTimeOffset]::Parse([string] $entry.expiresUtc) -le [DateTimeOffset]::UtcNow) {
    throw "Dependency age exception for ${Ecosystem}:${Name}@${Version} expired at $($entry.expiresUtc)."
  }
  Write-Warning "Using dependency age exception for ${Ecosystem}:${Name}@${Version}: $($entry.reason)"
  return $true
}

function Add-Result {
  param(
    [Parameter(Mandatory)][string] $Ecosystem,
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][DateTimeOffset] $Published
  )

  $age = [DateTimeOffset]::UtcNow - $Published
  $verified.Add([pscustomobject]@{
      Ecosystem = $Ecosystem
      Dependency = $Name
      Version = $Version
      PublishedUtc = $Published.UtcDateTime.ToString('u')
      AgeDays = [math]::Floor($age.TotalDays)
    })
  if ($Published -gt $cutoff -and -not (Test-Exception -Ecosystem $Ecosystem -Name $Name -Version $Version)) {
    $violations.Add("${Ecosystem}:${Name}@${Version} was published $($Published.UtcDateTime.ToString('u')), after cutoff $($cutoff.UtcDateTime.ToString('u')).")
  }
}

function Get-McrImageCreated {
  param(
    [Parameter(Mandatory)][string] $Repository,
    [Parameter(Mandatory)][string] $Reference
  )

  $headers = @{
    Accept = 'application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json'
  }
  $manifest = Invoke-RestMethod "https://mcr.microsoft.com/v2/$Repository/manifests/$Reference" -Headers $headers
  if ($null -ne $manifest.PSObject.Properties['manifests']) {
    $platformManifest = @($manifest.manifests | Where-Object {
        $_.platform.os -eq 'linux' -and $_.platform.architecture -eq 'amd64'
      } | Select-Object -First 1)
    if ($platformManifest.Count -eq 0) {
      throw "MCR image 'mcr.microsoft.com/$Repository@$Reference' has no linux/amd64 manifest."
    }
    $manifest = Invoke-RestMethod "https://mcr.microsoft.com/v2/$Repository/manifests/$($platformManifest[0].digest)" -Headers $headers
  }

  $config = Invoke-RestMethod "https://mcr.microsoft.com/v2/$Repository/blobs/$($manifest.config.digest)"
  return [DateTimeOffset]::Parse([string] $config.created)
}

function Get-WorkflowContainerImages {
  param([Parameter(Mandatory)][IO.FileInfo] $WorkflowFile)

  $containerIndent = -1
  foreach ($line in Get-Content -LiteralPath $WorkflowFile.FullName) {
    $indent = $line.Length - $line.TrimStart().Length
    if ($line -match '^\s*container:\s*["'']?(?<reference>[^\s#"'']*)') {
      if (-not [string]::IsNullOrWhiteSpace($Matches.reference)) {
        $Matches.reference
        continue
      }
      $containerIndent = $indent
      continue
    }
    if ($containerIndent -ge 0 -and -not [string]::IsNullOrWhiteSpace($line) -and $indent -le $containerIndent) {
      $containerIndent = -1
    }
    if ($containerIndent -ge 0 -and $line -match '^\s*image:\s*["'']?(?<reference>[^\s#"'']+)') {
      $Matches.reference
    }
  }
}

$pnpm = Get-Command pnpm -ErrorAction Stop
& $pnpm.Source install --frozen-lockfile --lockfile-only
if ($LASTEXITCODE -ne 0) {
  throw 'pnpm lockfile failed the configured minimum-release-age policy.'
}

$projectFiles = Get-ChildItem -Path (Join-Path $PSScriptRoot '..') -Filter '*.csproj' -Recurse |
  Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]'
foreach ($projectFile in $projectFiles) {
  [xml] $project = Get-Content -LiteralPath $projectFile.FullName -Raw
  foreach ($reference in @($project.SelectNodes('//PackageReference'))) {
    $name = ([string] $reference.Include).ToLowerInvariant()
    $version = [string] $reference.Version
    if ([string]::IsNullOrWhiteSpace($version) -or $version -match '[\[\]\(\),*]') {
      throw "NuGet dependency '$name' must use one exact version for age verification."
    }
    $leaf = Invoke-RestMethod "https://api.nuget.org/v3/registration5-gz-semver2/$name/$($version.ToLowerInvariant()).json"
    Add-Result -Ecosystem NuGet -Name $name -Version $version -Published ([DateTimeOffset]::Parse([string] $leaf.published))
  }
}

$workflowFiles = Get-ChildItem -Path (Join-Path $PSScriptRoot '../.github/workflows') -Filter '*.yml' -File
$actionReferences = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($workflowFile in $workflowFiles) {
  $content = Get-Content -LiteralPath $workflowFile.FullName -Raw
  foreach ($match in [regex]::Matches($content, 'uses:\s*["'']?(?<repo>[^\s@"'']+/[^\s@"'']+)@(?<ref>[^\s#"'']+)')) {
    $repository = $match.Groups['repo'].Value
    $ref = $match.Groups['ref'].Value
    $null = $actionReferences.Add("$repository@$ref")
  }
}
foreach ($actionReference in $actionReferences) {
  $separator = $actionReference.LastIndexOf('@')
  $repository = $actionReference.Substring(0, $separator)
  $ref = $actionReference.Substring($separator + 1)
  if ($ref -notmatch '^[0-9a-f]{40}$') {
    throw "GitHub Action '$repository@$ref' must use an immutable 40-character commit SHA."
  }
  $sha = $ref
  $patch = (Invoke-WebRequest "https://github.com/$repository/commit/$sha.patch").Content
  $dateLine = ($patch -split "`n" | Where-Object { $_ -like 'Date: *' } | Select-Object -First 1)
  if ([string]::IsNullOrWhiteSpace($dateLine)) {
    throw "GitHub Action '$repository@$ref' did not expose a commit date."
  }
  $published = [DateTimeOffset]::Parse($dateLine.Substring(6).Trim())
  Add-Result -Ecosystem GitHubActions -Name $repository -Version $ref -Published $published
}

$workflowImages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($workflowFile in $workflowFiles) {
  foreach ($imageReference in Get-WorkflowContainerImages -WorkflowFile $workflowFile) {
    $null = $workflowImages.Add($imageReference)
  }
}
foreach ($imageReference in $workflowImages) {
  if ($imageReference -notmatch '^(?<registry>[^/]+)/(?<repository>[^@]+)@(?<digest>sha256:[0-9a-fA-F]{64})$') {
    throw "Workflow container image must use an explicit registry and digest: $imageReference"
  }
  if ($Matches.registry -ne 'mcr.microsoft.com') {
    throw "Dependency age validation does not support workflow container registry '$($Matches.registry)'."
  }
  $repositoryWithTag = $Matches.repository
  $digest = $Matches.digest
  $repository = $repositoryWithTag -replace ':[^/:]+$', ''
  Add-Result -Ecosystem Docker -Name "mcr.microsoft.com/$repository" -Version $digest `
    -Published (Get-McrImageCreated -Repository $repository -Reference $digest)
}

$dockerfile = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Dockerfile') -Raw
foreach ($match in [regex]::Matches($dockerfile, '(?m)^FROM\s+(?<image>[^\s]+)')) {
  $imageReference = $match.Groups['image'].Value
  if ($imageReference -notmatch '^(?<registry>[^/]+)/(?<repository>.+):(?<tag>[^@]+)$') {
    throw "Docker base image must use an explicit registry and tag: $imageReference"
  }
  if ($Matches.registry -ne 'mcr.microsoft.com') {
    throw "Dependency age validation does not support Docker registry '$($Matches.registry)'."
  }
  $repository = $Matches.repository
  $tag = $Matches.tag
  Add-Result -Ecosystem Docker -Name "mcr.microsoft.com/$repository" -Version $tag `
    -Published (Get-McrImageCreated -Repository $repository -Reference $tag)
}

$verified | Sort-Object Ecosystem, Dependency | Format-Table -AutoSize | Out-Host
if ($violations.Count -ne 0) {
  throw "Dependency age policy failed:$([Environment]::NewLine)$($violations -join [Environment]::NewLine)"
}
Write-Host "All dependency versions are at least $MinimumAgeDays complete days old."
