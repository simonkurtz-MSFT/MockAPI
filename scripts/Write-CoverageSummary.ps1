[CmdletBinding()]
param(
  [Parameter(Mandatory)][string] $BackendReport,
  [Parameter(Mandatory)][string] $FrontendReport,
  [Parameter(Mandatory)][string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CoverageCounter {
  param(
    [Parameter(Mandatory)][Xml.XmlElement] $Root,
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $Path
  )

  $attribute = $Root.GetAttributeNode($Name)
  $value = 0
  if ($null -eq $attribute -or -not [int]::TryParse($attribute.Value, [ref] $value) -or $value -lt 0) {
    throw "Coverage report '$Path' has a missing or invalid '$Name' counter."
  }
  return $value
}

function Get-CoverageMetrics {
  param([Parameter(Mandatory)][string] $Path)

  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Coverage report was not found: $Path"
  }
  [xml] $coverage = Get-Content -LiteralPath $Path -Raw
  $root = $coverage.DocumentElement
  $metrics = [pscustomobject]@{
    LinesValid = Get-CoverageCounter -Root $root -Name 'lines-valid' -Path $Path
    LinesCovered = Get-CoverageCounter -Root $root -Name 'lines-covered' -Path $Path
    BranchesValid = Get-CoverageCounter -Root $root -Name 'branches-valid' -Path $Path
    BranchesCovered = Get-CoverageCounter -Root $root -Name 'branches-covered' -Path $Path
  }
  if ($metrics.LinesCovered -gt $metrics.LinesValid -or $metrics.BranchesCovered -gt $metrics.BranchesValid) {
    throw "Coverage report '$Path' has covered counts greater than valid counts."
  }
  return $metrics
}

function Get-Percent {
  param([int] $Covered, [int] $Valid)
  if ($Valid -eq 0) { return 100 }
  return [math]::Round(100 * $Covered / $Valid, 2)
}

$backend = Get-CoverageMetrics -Path $BackendReport
$frontend = Get-CoverageMetrics -Path $FrontendReport
$merged = [pscustomobject]@{
  LinesValid = $backend.LinesValid + $frontend.LinesValid
  LinesCovered = $backend.LinesCovered + $frontend.LinesCovered
  BranchesValid = $backend.BranchesValid + $frontend.BranchesValid
  BranchesCovered = $backend.BranchesCovered + $frontend.BranchesCovered
}

$outputDirectory = Split-Path -Parent $Output
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
  New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$rows = @(
  '# Coverage summary',
  '',
  '| Scope | Lines | Branches |',
  '| --- | ---: | ---: |',
  "| Backend | $(Get-Percent $backend.LinesCovered $backend.LinesValid)% ($($backend.LinesCovered)/$($backend.LinesValid)) | $(Get-Percent $backend.BranchesCovered $backend.BranchesValid)% ($($backend.BranchesCovered)/$($backend.BranchesValid)) |",
  "| Frontend | $(Get-Percent $frontend.LinesCovered $frontend.LinesValid)% ($($frontend.LinesCovered)/$($frontend.LinesValid)) | $(Get-Percent $frontend.BranchesCovered $frontend.BranchesValid)% ($($frontend.BranchesCovered)/$($frontend.BranchesValid)) |",
  "| Weighted total | $(Get-Percent $merged.LinesCovered $merged.LinesValid)% ($($merged.LinesCovered)/$($merged.LinesValid)) | $(Get-Percent $merged.BranchesCovered $merged.BranchesValid)% ($($merged.BranchesCovered)/$($merged.BranchesValid)) |"
)
Set-Content -LiteralPath $Output -Value $rows -Encoding utf8
Write-Host "Coverage summary written to $Output."
