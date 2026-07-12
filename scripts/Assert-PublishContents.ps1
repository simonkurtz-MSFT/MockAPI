[CmdletBinding()]
param([Parameter(Mandatory)][string] $PublishDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
  throw "Publish directory was not found: $PublishDirectory"
}

$forbiddenNames = @(
  'node_modules',
  'tests',
  'playwright.config.js',
  'vitest.config.js',
  'package.json',
  'pnpm-lock.yaml',
  'pnpm-workspace.yaml'
)
$forbiddenExtensions = @('.pdb', '.trx', '.lcov')
$violations = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -Force | Where-Object {
  $forbiddenNames -contains $_.Name -or $forbiddenExtensions -contains $_.Extension.ToLowerInvariant()
}

if ($violations) {
  $paths = $violations.FullName -join [Environment]::NewLine
  throw "Development or test artifacts entered the publish output:$([Environment]::NewLine)$paths"
}

$application = Get-ChildItem -LiteralPath $PublishDirectory -Filter 'MockAPI*' -File
if (-not $application) {
  throw "The MockAPI application was not found in publish output: $PublishDirectory"
}

$size = (Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host "Publish contents accepted: $PublishDirectory ($size bytes)"
