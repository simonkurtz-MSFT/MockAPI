[CmdletBinding()]
param(
  [string] $BaseUri = 'http://localhost:8080',
  [switch] $VerifyPersistence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$BaseUri = $BaseUri.TrimEnd('/')

function Invoke-Probe {
  param(
    [string] $Path,
    [string] $Method = 'Get',
    [int] $ExpectedStatus = 200,
    [hashtable] $Headers = @{},
    [string] $Body
  )

  $parameters = @{
    Uri = "$BaseUri$Path"
    Method = $Method
    Headers = $Headers
    TimeoutSec = 20
    SkipHttpErrorCheck = $true
  }
  if ($PSBoundParameters.ContainsKey('Body')) {
    $parameters.Body = $Body
    $parameters.ContentType = 'application/json'
  }
  $response = Invoke-WebRequest @parameters
  $content = if ($response.Content -is [byte[]]) {
    [Text.Encoding]::UTF8.GetString($response.Content)
  }
  else {
    $response.Content
  }
  if ($response.StatusCode -ne $ExpectedStatus) {
    throw "$Method $Path returned $($response.StatusCode), expected $ExpectedStatus. $content"
  }
  if ($response.Headers.ContainsKey('Server')) {
    throw "$Method $Path exposed a Server header."
  }
  return [pscustomobject]@{ Content = $content; StatusCode = $response.StatusCode }
}

$ready = (Invoke-Probe '/health/ready').Content | ConvertFrom-Json
if ($ready.status -ne 'ready') {
  throw 'The published application is not ready.'
}
$null = Invoke-Probe '/health/live'

if ($VerifyPersistence) {
  $mock = Invoke-Probe '/aot-check'
  $status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
  if ($mock.Content -ne 'persisted native response' -or $status.hasUnsavedChanges) {
    throw 'Saved configuration did not survive container recreation.'
  }
  Write-Host 'Published application persistence passed.'
  return
}

$dashboard = Invoke-Probe '/'
if ($dashboard.Content -notmatch '<title>MockAPI Console</title>') {
  throw 'The dashboard was not served.'
}
foreach ($path in @('/app.css', '/app.js', '/__mockapi/swagger/index.html', '/__mockapi/swagger/swagger-ui-bundle.js')) {
  $null = Invoke-Probe $path
}
$openApi = (Invoke-Probe '/__mockapi/openapi/v1.json').Content | ConvertFrom-Json
$operation = $openApi.paths.'/__mockapi/api/configuration/export/{format}'.get
if ($operation.operationId -ne 'ExportConfigurationFormat' -or
    'download' -notin $operation.parameters.name) {
  throw 'OpenAPI is missing the export operation or its nullable query parameter.'
}

$endpointId = '529fdfb8-4942-4a44-a3df-266771e4b53f'
$endpoint = @{
  id = $endpointId
  name = 'Native publication probe'
  enabled = $true
  methods = @('GET', 'HEAD')
  path = '/aot-check'
  response = @{
    statusCode = 200
    headers = @{}
    contentType = 'text/plain'
    body = 'native response'
  }
}
$status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
$staleEtag = $status.etag
$null = Invoke-Probe '/__mockapi/api/endpoints' -Method Post -ExpectedStatus 201 `
  -Headers @{ 'If-Match' = $status.etag } -Body ($endpoint | ConvertTo-Json -Depth 10)
if ((Invoke-Probe '/aot-check').Content -ne 'native response') {
  throw 'A runtime-created endpoint did not dispatch.'
}
$null = Invoke-Probe '/aot-check' -Method Head
$null = Invoke-Probe '/aot-check' -Method Post -ExpectedStatus 404
$null = Invoke-Probe "/__mockapi/api/endpoints/$endpointId/enabled" -Method Put -ExpectedStatus 412 `
  -Headers @{ 'If-Match' = $staleEtag } -Body '{"enabled":false}'

$status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
$null = Invoke-Probe '/__mockapi/api/endpoints/bulk' -Method Post -ExpectedStatus 204 `
  -Headers @{ 'If-Match' = $status.etag } `
  -Body (@{ endpointIds = @($endpointId); operation = 'disable' } | ConvertTo-Json)
$null = Invoke-Probe '/aot-check' -ExpectedStatus 404
$status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
$endpoint.response.body = 'persisted native response'
$null = Invoke-Probe "/__mockapi/api/endpoints/$endpointId" -Method Put `
  -Headers @{ 'If-Match' = $status.etag } -Body ($endpoint | ConvertTo-Json -Depth 10)

$export = Invoke-Probe '/__mockapi/api/configuration/export'
$validation = (Invoke-Probe '/__mockapi/api/configuration/validate' -Method Post -Body $export.Content).Content | ConvertFrom-Json
if (-not $validation.isValid) {
  throw 'Exported configuration did not validate.'
}
$status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
$null = Invoke-Probe '/__mockapi/api/configuration/import' -Method Put `
  -Headers @{ 'If-Match' = $status.etag } -Body $export.Content
foreach ($format in @('postman', 'insomnia', 'openapi', 'curl', 'jmeter', 'k6', 'http')) {
  $artifact = Invoke-Probe "/__mockapi/api/configuration/export/${format}?download=false"
  if ([string]::IsNullOrWhiteSpace($artifact.Content)) {
    throw "The $format export was empty."
  }
}

$statistics = (Invoke-Probe '/__mockapi/api/statistics').Content | ConvertFrom-Json
if ($statistics.totalRequests -lt 4) {
  throw 'Native request statistics were not recorded.'
}
$null = Invoke-Probe '/__mockapi/api/statistics/reset' -Method Post -ExpectedStatus 204
$status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
$null = Invoke-Probe '/__mockapi/api/configuration/example/merge' -Method Post -Headers @{ 'If-Match' = $status.etag }
$port = ([Uri] $BaseUri).Port
& (Join-Path $PSScriptRoot '../start.ps1') -Action container-showcase -Port $port -SkipContainerCheck
if (-not $?) {
  throw 'Published built-in example showcase failed.'
}
$status = (Invoke-Probe '/__mockapi/api/configuration').Content | ConvertFrom-Json
$saved = (Invoke-Probe '/__mockapi/api/configuration/save' -Method Post -Headers @{ 'If-Match' = $status.etag }).Content | ConvertFrom-Json
if (-not $saved.isCurrentRevision) {
  throw 'The native application did not persist the current configuration.'
}
Write-Host 'Published application routes, OpenAPI, exports, mutations, statistics, and save passed.'
