[CmdletBinding()]
param([string] $WorkflowDirectory = (Join-Path $PSScriptRoot '../.github/workflows'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $WorkflowDirectory -PathType Container)) {
  throw "Workflow directory was not found: $WorkflowDirectory"
}

$workflowFiles = Get-ChildItem -LiteralPath $WorkflowDirectory -File |
  Where-Object Extension -In '.yml', '.yaml'
$violations = [Collections.Generic.List[string]]::new()
$referenceCount = 0

foreach ($workflowFile in $workflowFiles) {
  $lineNumber = 0
  foreach ($line in Get-Content -LiteralPath $workflowFile.FullName) {
    $lineNumber++
    $match = [regex]::Match($line, '^\s*-?\s*uses:\s*["'']?(?<reference>[^\s#"'']+)')
    if (-not $match.Success) {
      continue
    }

    $referenceCount++
    $reference = $match.Groups['reference'].Value
    if ($reference.StartsWith('./', [StringComparison]::Ordinal)) {
      continue
    }

    $isPinnedAction = $reference -match '^[^/@\s]+/[^@\s]+@[0-9a-fA-F]{40}$'
    $isPinnedContainer = $reference -match '^docker://[^@\s]+@sha256:[0-9a-fA-F]{64}$'
    if (-not $isPinnedAction -and -not $isPinnedContainer) {
      $relativePath = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '..'), $workflowFile.FullName)
      $violations.Add("${relativePath}:${lineNumber}: '$reference' must use an immutable commit SHA or image digest.")
    }
  }
}

if ($violations.Count -ne 0) {
  throw "Workflow pin validation failed:$([Environment]::NewLine)$($violations -join [Environment]::NewLine)"
}

Write-Host "Workflow pin validation passed for $referenceCount references in $($workflowFiles.Count) files."
