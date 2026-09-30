[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $PublishDirectory,
  [switch] $NativeAot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
  throw "Publish directory was not found: $PublishDirectory"
}

$forbiddenNames = @(
  'node_modules',
  'tests',
  'appsettings.Development.json',
  'playwright.config.js',
  'vitest.config.js',
  'package.json',
  'pnpm-lock.yaml',
  'pnpm-workspace.yaml'
)
$forbiddenExtensions = @('.pdb', '.dbg', '.trx', '.lcov')
$violations = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -Force | Where-Object {
  $forbiddenNames -contains $_.Name -or $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or
  $_.Name -match '\.security\.json(?:\..*\.tmp)?$'
}

if ($violations) {
  $paths = $violations.FullName -join [Environment]::NewLine
  throw "Development or test artifacts entered the publish output:$([Environment]::NewLine)$paths"
}

$application = Get-ChildItem -LiteralPath $PublishDirectory -Filter 'MockAPI*' -File
if (-not $application) {
  throw "The MockAPI application was not found in publish output: $PublishDirectory"
}

if ($NativeAot) {
  $executable = Join-Path $PublishDirectory 'MockAPI'
  if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'The native Linux executable is missing.'
  }
  $stream = [IO.File]::OpenRead($executable)
  try {
    $header = [byte[]]::new(4)
    $read = $stream.Read($header, 0, $header.Length)
    if ($read -ne 4 -or [Convert]::ToHexString($header) -ne '7F454C46') {
      throw 'The native Linux executable must have an ELF header.'
    }
  }
  finally {
    $stream.Dispose()
  }
  $managedRuntime = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -match '^lib(coreclr|clrjit|hostfxr|hostpolicy)\.' -or
    $_.Name -match '\.(deps|runtimeconfig)\.json$'
  }
  if ($managedRuntime) {
    throw "Managed runtime artifacts entered the AOT publication: $($managedRuntime.Name -join ', ')"
  }
}

$size = (Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host "Publish contents accepted: $PublishDirectory ($size bytes)"
