[CmdletBinding()]
param(
  [Parameter(Mandatory)][string[]] $Report
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$violations = [Collections.Generic.List[string]]::new()
$totalTests = 0

function Get-AttributeTotal {
  param(
    [Parameter(Mandatory)][object[]] $Elements,
    [Parameter(Mandatory)][string] $Name,
    [switch] $Required
  )

  $sum = 0
  foreach ($element in $Elements) {
    $attribute = $element.Attributes[$Name]
    if ($null -eq $attribute) {
      if ($Required) {
        throw "Test result is missing required '$Name' counter."
      }
      continue
    }
    $value = 0
    if (-not [int]::TryParse($attribute.Value, [ref] $value) -or $value -lt 0) {
      throw "Test result has invalid '$Name' counter '$($attribute.Value)'."
    }
    $sum += $value
  }
  return $sum
}

foreach ($reportPath in $Report) {
  if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    $violations.Add("Test result was not found: $reportPath")
    continue
  }

  [xml] $results = Get-Content -LiteralPath $reportPath -Raw
  $root = $results.DocumentElement
  if ($root.LocalName -eq 'TestRun') {
    $counters = $root.SelectSingleNode("*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters) {
      throw "TRX result '$reportPath' is missing ResultSummary/Counters."
    }
    $tests = Get-AttributeTotal -Elements @($counters) -Name 'total' -Required
    $failed = (Get-AttributeTotal -Elements @($counters) -Name 'failed') + (Get-AttributeTotal -Elements @($counters) -Name 'error') + (Get-AttributeTotal -Elements @($counters) -Name 'timeout') + (Get-AttributeTotal -Elements @($counters) -Name 'aborted')
    $skipped = (Get-AttributeTotal -Elements @($counters) -Name 'notExecuted') + (Get-AttributeTotal -Elements @($counters) -Name 'inconclusive') + (Get-AttributeTotal -Elements @($counters) -Name 'notRunnable') + (Get-AttributeTotal -Elements @($counters) -Name 'disconnected') + (Get-AttributeTotal -Elements @($counters) -Name 'warning')
  }
  elseif ($root.LocalName -in 'testsuite', 'testsuites') {
    $suites = if ($root.LocalName -eq 'testsuite') { @($root) } else { @($root.testsuite) }
    if ($suites.Count -eq 0) {
      throw "JUnit result '$reportPath' contains no test suites."
    }
    $tests = Get-AttributeTotal -Elements $suites -Name 'tests' -Required
    $failed = (Get-AttributeTotal -Elements $suites -Name 'failures') + (Get-AttributeTotal -Elements $suites -Name 'errors')
    $skipped = (Get-AttributeTotal -Elements $suites -Name 'skipped') + (Get-AttributeTotal -Elements $suites -Name 'disabled')
  }
  else {
    $violations.Add("Unsupported test result format in '$reportPath'.")
    continue
  }

  $totalTests += $tests
  if ($tests -eq 0) {
    $violations.Add("Test result '$reportPath' contains no tests.")
  }
  if ($failed -ne 0) {
    $violations.Add("Test result '$reportPath' contains $failed failed tests.")
  }
  if ($skipped -ne 0) {
    $violations.Add("Test result '$reportPath' contains $skipped unexpectedly skipped tests.")
  }
}

if ($violations.Count -ne 0) {
  throw "Test result validation failed:$([Environment]::NewLine)$($violations -join [Environment]::NewLine)"
}

Write-Host "Test result validation passed for $totalTests tests across $($Report.Count) reports."
