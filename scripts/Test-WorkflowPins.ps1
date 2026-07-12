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
$jobCount = 0

foreach ($workflowFile in $workflowFiles) {
  $lineNumber = 0
  $containerIndent = -1
  foreach ($line in Get-Content -LiteralPath $workflowFile.FullName) {
    $lineNumber++
    $indent = $line.Length - $line.TrimStart().Length
    $containerMatch = [regex]::Match($line, '^\s*container:\s*["'']?(?<reference>[^\s#"'']*)')
    if ($containerMatch.Success) {
      $reference = $containerMatch.Groups['reference'].Value
      if (-not [string]::IsNullOrWhiteSpace($reference)) {
        $referenceCount++
        if ($reference -notmatch '^[^@\s]+@sha256:[0-9a-fA-F]{64}$') {
          $relativePath = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '..'), $workflowFile.FullName)
          $violations.Add("${relativePath}:${lineNumber}: '$reference' must use an immutable image digest.")
        }
        continue
      }
      $containerIndent = $indent
      continue
    }
    if ($containerIndent -ge 0 -and -not [string]::IsNullOrWhiteSpace($line) -and $indent -le $containerIndent) {
      $containerIndent = -1
    }

    $usesMatch = [regex]::Match($line, '^\s*-?\s*uses:\s*["'']?(?<reference>[^\s#"'']+)')
    $imageMatch = if ($containerIndent -ge 0) {
      [regex]::Match($line, '^\s*image:\s*["'']?(?<reference>[^\s#"'']+)')
    }
    else {
      [Text.RegularExpressions.Match]::Empty
    }
    if ($usesMatch.Success) {
      $reference = $usesMatch.Groups['reference'].Value
      $referenceCount++
      if ($reference.StartsWith('./', [StringComparison]::Ordinal)) {
        continue
      }

      $isPinnedAction = $reference -match '^[^/@\s]+/[^@\s]+@[0-9a-fA-F]{40}$'
      $isPinnedContainer = $reference -match '^docker://[^@\s]+@sha256:[0-9a-fA-F]{64}$'
      if ($isPinnedAction -or $isPinnedContainer) {
        continue
      }
    }
    elseif ($imageMatch.Success) {
      $reference = $imageMatch.Groups['reference'].Value
      $referenceCount++
      if ($reference -match '^[^@\s]+@sha256:[0-9a-fA-F]{64}$') {
        continue
      }
    }
    else {
      continue
    }

    if (-not [string]::IsNullOrWhiteSpace($reference)) {
      $relativePath = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '..'), $workflowFile.FullName)
      $violations.Add("${relativePath}:${lineNumber}: '$reference' must use an immutable commit SHA or image digest.")
    }
  }

  $inJobs = $false
  $jobName = $null
  $jobLineNumber = 0
  $hasTimeout = $false
  $lineNumber = 0
  foreach ($line in Get-Content -LiteralPath $workflowFile.FullName) {
    $lineNumber++
    if ($line -match '^jobs:\s*$') {
      $inJobs = $true
      continue
    }
    if (-not $inJobs) {
      continue
    }
    if ($line -match '^  (?<name>[A-Za-z0-9_-]+):\s*$') {
      if ($null -ne $jobName -and -not $hasTimeout) {
        $relativePath = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '..'), $workflowFile.FullName)
        $violations.Add("${relativePath}:${jobLineNumber}: job '$jobName' must define timeout-minutes between 1 and 60.")
      }
      $jobName = $Matches.name
      $jobLineNumber = $lineNumber
      $hasTimeout = $false
      $jobCount++
      continue
    }
    if ($null -ne $jobName -and $line -match '^    timeout-minutes:\s*(?<minutes>[^\s#]+)') {
      $hasTimeout = $true
      $minutes = 0
      if (-not [int]::TryParse($Matches.minutes, [ref] $minutes) -or $minutes -lt 1 -or $minutes -gt 60) {
        $relativePath = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '..'), $workflowFile.FullName)
        $violations.Add("${relativePath}:${lineNumber}: job '$jobName' timeout-minutes must be an integer between 1 and 60.")
      }
    }
  }
  if ($null -ne $jobName -and -not $hasTimeout) {
    $relativePath = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot '..'), $workflowFile.FullName)
    $violations.Add("${relativePath}:${jobLineNumber}: job '$jobName' must define timeout-minutes between 1 and 60.")
  }
}

if ($violations.Count -ne 0) {
  throw "Workflow pin validation failed:$([Environment]::NewLine)$($violations -join [Environment]::NewLine)"
}

Write-Host "Workflow policy validation passed for $referenceCount references and $jobCount jobs in $($workflowFiles.Count) files."
