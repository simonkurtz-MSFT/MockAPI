#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testDirectory = Join-Path $repositoryRoot ".developer-cli-domains-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $testDirectory
$bashScript = Join-Path $testDirectory 'test.sh'

function Assert-True {
  param([bool] $Condition, [string] $Message)
  if (-not $Condition) { throw $Message }
}

function New-EnvironmentFixture {
  param([string[]] $Settings)

  # Never overwrite a fixture that a previous shell invocation may still have open.
  $path = Join-Path $testDirectory "settings-$([Guid]::NewGuid().ToString('N')).env"
  Set-Content -LiteralPath $path -Value $Settings -Encoding utf8NoBOM
  return $path
}

try {
  $tokens = $null
  $errors = $null
  $ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repositoryRoot 'start.ps1'), [ref] $tokens, [ref] $errors
  )
  Assert-True ($errors.Count -eq 0) "PowerShell parse errors: $errors"
  foreach ($name in @(
      'Get-AzureDeploymentConfiguration', 'Get-AzureCliValue', 'Get-AzureContainerAppId',
      'Save-AzureDomainBindings', 'Invoke-AzureCustomDomainCommand', 'Set-AzureCustomDomain',
      'Complete-AzureDeployment', 'Write-Field'
    )) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
      }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
  }
  $fieldWidth = 20
  $Action = 'azure-deploy'
  $requiredAzureEnvironmentKeys = @('AZURE_SUBSCRIPTION_ID', 'AZURE_LOCATION', 'AZURE_ENV_NAME')
  $azureEnvironmentKeys = $requiredAzureEnvironmentKeys + @(
    'AZURE_CUSTOM_DOMAIN', 'AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD',
    'AZURE_DASHBOARD_USERNAME', 'AZURE_DASHBOARD_PASSWORD'
  )
  $savedEnvironment = @{}
  foreach ($key in $azureEnvironmentKeys) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $null)
  }
  $baseSettings = @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2', 'AZURE_ENV_NAME=domain-test'
  )
  $bashSource = Get-Content (Join-Path $repositoryRoot 'start.sh') -Raw
  $bashFunctions = foreach ($name in @(
      'trim', 'write_field', 'configuration_value', 'get_azure_deployment_configuration', 'get_azure_cli_value',
      'get_azure_container_app_id', 'save_azure_domain_bindings',
      'invoke_azure_custom_domain_command', 'set_azure_custom_domain', 'complete_azure_deployment'
    )) {
    $match = [regex]::Match($bashSource, "(?ms)^$name\(\) \{.*?^\}")
    Assert-True $match.Success "Missing Bash function $name"
    $match.Value
  }
  $bashHarness = @'
#!/usr/bin/env bash
set -Eeuo pipefail
REPOSITORY_ROOT="$PWD"
ENVIRONMENT_FILE="$1"
scenario="$2"
ACTION='azure-deploy'
COLOR_RESET=""
COLOR_GREEN=""
COLOR_YELLOW=""
COLOR_RED=""
COLOR_CYAN=""
unset AZURE_SUBSCRIPTION_ID AZURE_LOCATION AZURE_ENV_NAME AZURE_CUSTOM_DOMAIN AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD AZURE_DASHBOARD_USERNAME AZURE_DASHBOARD_PASSWORD
if [[ "$scenario" == 'override' ]]; then export AZURE_CUSTOM_DOMAIN=PROCESS.example.com; fi
die() { printf '%s\n' "$1" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1; }
write_color() { printf '%s\n' "$2"; }
run_tool() { shift; "$@"; }
azd() { printf 'MUTATE azd %s\n' "$*"; }
test_azure_deployment() { printf 'READY %s\n' "${2:-https://generated.example.test}"; }
az() {
  if [[ "$scenario" == 'read-failure' ]]; then return 1; fi
  case "$1 $2" in
    'resource list')
    [[ "$*" == *'--tag azd-service-name=mockapi'* ]] || return 96
    [[ "$*" != *'--resource-type'* ]] || return 97
    [[ "$*" == *'Microsoft.App/containerApps'* ]] || return 98
    if [[ "$scenario" != 'save-new' ]]; then printf '/subscriptions/fixture/resourceGroups/rg-domain-test-eastus2/providers/Microsoft.App/containerApps/app\n'; fi;;
    'containerapp show')
      if [[ "$*" == *--query* ]]; then
        if [[ "$scenario" == 'invalid-bindings' ]]; then printf '{}'; else printf '[{"name":"old.example.com","bindingType":"SniEnabled","certificateId":"/managedCertificates/old"}]'; fi
      else
        local binding=''
        case "$scenario" in
          existing) binding='{"name":"api.example.com","bindingType":"SniEnabled","certificateId":"/managedCertificates/cert"}';;
          foreign) binding='{"name":"api.example.com","bindingType":"SniEnabled","certificateId":"/certificates/cert"}';;
          pending) binding='{"name":"api.example.com","bindingType":"Disabled","certificateId":null}';;
        esac
        printf '{"name":"app","properties":{"environmentId":"/environment/id","customDomainVerificationId":"verification","configuration":{"ingress":{"fqdn":"generated.example.test","customDomains":[%s]}}}}\n' "$binding"
      fi;;
    'containerapp env') printf '192.0.2.1\n';;
    'containerapp hostname')
      printf 'MUTATE az %s\n' "$*"
      if [[ "$scenario" == 'dns-pending-add' && "$3" == 'add' ]]; then
        printf 'ERROR: (InvalidCustomHostNameValidation) DNS records were not found.\n' >&2
        return 1
      fi
      if [[ "$scenario" == 'dns-pending-bind' && "$3" == 'bind' ]]; then
        printf 'ERROR: (InvalidCustomHostNameValidation) DNS records were not found.\n' >&2
        return 1
      fi
      if [[ "$scenario" == 'bind-failure' && "$3" == 'bind' ]]; then
        printf 'Certificate issuance failed.\n' >&2
        return 1
      fi
      if [[ "$scenario" == 'add-failure' && "$3" == 'add' ]]; then
        printf 'Authorization failed.\n' >&2
        return 1
      fi
      printf 'Hostname operation completed.\n';;
    *) return 1;;
  esac
}
'@
  $bashRun = @'
AZURE_CONFIGURATION="$(get_azure_deployment_configuration)"
AZURE_CONFIGURATION_ENVIRONMENT_NAME="$(configuration_value "$AZURE_CONFIGURATION" AZURE_ENV_NAME)"
AZURE_CONFIGURATION_LOCATION="$(configuration_value "$AZURE_CONFIGURATION" AZURE_LOCATION)"
AZURE_CONFIGURATION_SUBSCRIPTION="$(configuration_value "$AZURE_CONFIGURATION" AZURE_SUBSCRIPTION_ID)"
ENVIRONMENT_FILE="settings with 'quotes'.env"
case "$scenario" in
  config|override) printf '%s\n' "$AZURE_CONFIGURATION";;
  save|save-new|invalid-bindings) save_azure_domain_bindings;;
  *) complete_azure_deployment;;
esac
'@
  Set-Content $bashScript ($bashHarness + "`n" + ($bashFunctions -join "`n") + "`n" + $bashRun + "`n").Replace("`r", '') -Encoding utf8NoBOM -NoNewline

  function Invoke-BashTest {
    param([string] $Scenario, [string] $SettingsPath)
    $relativeDirectory = Split-Path -Leaf $testDirectory
    $settingsName = Split-Path -Leaf $SettingsPath
    $output = & bash "./$relativeDirectory/test.sh" "./$relativeDirectory/$settingsName" $Scenario 2>&1
    return @{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
  }

  $previousFixture = New-EnvironmentFixture ($baseSettings + 'AZURE_CUSTOM_DOMAIN=previous.example.com')
  $reader = [IO.File]::Open($previousFixture, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
  try {
    $environmentFile = New-EnvironmentFixture ($baseSettings + 'AZURE_CUSTOM_DOMAIN=next.example.com')
    Assert-True ($environmentFile -ne $previousFixture) 'Each case must use a distinct settings fixture.'
    Assert-True ((Get-AzureDeploymentConfiguration).AZURE_CUSTOM_DOMAIN -ceq 'next.example.com') 'PowerShell must read the new fixture while the previous one is locked.'
    $result = Invoke-BashTest config $environmentFile
    Assert-True ($result.ExitCode -eq 0 -and $result.Output.Contains('AZURE_CUSTOM_DOMAIN=next.example.com')) 'Bash must read the same new fixture while the previous one is locked.'
    Assert-True ((Get-Content -LiteralPath $previousFixture) -contains 'AZURE_CUSTOM_DOMAIN=previous.example.com') 'Previous settings fixtures must remain unchanged.'
  }
  finally { $reader.Dispose() }

  foreach ($domain in @('', 'API.Example.com', 'example.co.uk', 'https://api.example.com', '*.example.com',
      'api.example.com:443', 'example.com/path', 'example.com.', '192.0.2.1', '-bad.example.com',
      ('a' * 64 + '.example.com'), ('a.' * 126 + 'com'))) {
    $environmentFile = New-EnvironmentFixture ($baseSettings + "AZURE_CUSTOM_DOMAIN=$domain")
    $expectedValid = $domain -in @('', 'API.Example.com', 'example.co.uk')
    $configuration = $null
    $failure = $null
    try { $configuration = Get-AzureDeploymentConfiguration } catch { $failure = $_ }
    Assert-True (($null -eq $failure) -eq $expectedValid) "PowerShell hostname validation: $domain"
    $result = Invoke-BashTest config $environmentFile
    Assert-True (($result.ExitCode -eq 0) -eq $expectedValid) "Bash hostname validation: $domain $($result.Output)"
    if ($expectedValid) {
      Assert-True ($configuration.AZURE_CUSTOM_DOMAIN -ceq $domain.ToLowerInvariant()) 'PowerShell must normalize hostname case.'
      Assert-True ($result.Output.Contains("AZURE_CUSTOM_DOMAIN=$($domain.ToLowerInvariant())")) 'Bash must normalize hostname case.'
    }
  }
  foreach ($method in @('', 'CNAME', 'HTTP', 'http', 'TXT')) {
    $environmentFile = New-EnvironmentFixture ($baseSettings + 'AZURE_CUSTOM_DOMAIN=api.example.com' + "AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=$method")
    $failure = $null
    try { $null = Get-AzureDeploymentConfiguration } catch { $failure = $_ }
    Assert-True (($null -eq $failure) -eq ($method -cin @('', 'CNAME', 'HTTP'))) "PowerShell validation method mismatch: $method $failure"
    $result = Invoke-BashTest config $environmentFile
    Assert-True (($result.ExitCode -eq 0) -eq ($method -cin @('', 'CNAME', 'HTTP'))) "Bash validation method mismatch: $method"
  }
  $environmentFile = New-EnvironmentFixture ($baseSettings + 'AZURE_CUSTOM_DOMAIN=file.example.com')
  $env:AZURE_CUSTOM_DOMAIN = 'PROCESS.example.com'
  Assert-True ((Get-AzureDeploymentConfiguration).AZURE_CUSTOM_DOMAIN -ceq 'process.example.com') 'Process value must override file.'
  Assert-True ((Invoke-BashTest override $environmentFile).Output.Contains('AZURE_CUSTOM_DOMAIN=process.example.com')) 'Bash process value must override file.'
  Remove-Item Env:\AZURE_CUSTOM_DOMAIN

  function Invoke-NativeTool {
    param([string] $Executable, [string[]] $Arguments, [switch] $StreamOutput, [switch] $CaptureStandardError)
    $command = $Arguments -join ' '
    if ($scenario -eq 'read-failure') { return @{ ExitCode = 1; Output = @('read failed') } }
    if ($command.StartsWith('containerapp hostname')) {
      $script:events.Add("MUTATE az $command")
      Assert-True ($CaptureStandardError -and -not $StreamOutput) 'Hostname diagnostics must be captured before classifying expected DNS failures.'
    }
    if ($scenario -eq 'dns-pending-add' -and $command.StartsWith('containerapp hostname add')) {
      return @{ ExitCode = 1; Output = @('ERROR: (InvalidCustomHostNameValidation) DNS records were not found.') }
    }
    if ($scenario -eq 'dns-pending-bind' -and $command.StartsWith('containerapp hostname bind')) {
      return @{ ExitCode = 1; Output = @('ERROR: (InvalidCustomHostNameValidation) DNS records were not found.') }
    }
    if ($command.StartsWith('containerapp hostname')) {
      if ($scenario -eq 'bind-failure' -and $command.StartsWith('containerapp hostname bind')) {
        return @{ ExitCode = 1; Output = @('Certificate issuance failed.') }
      }
      if ($scenario -eq 'add-failure' -and $command.StartsWith('containerapp hostname add')) {
        return @{ ExitCode = 1; Output = @('Authorization failed.') }
      }
      return @{ ExitCode = 0; Output = @('Hostname operation completed.') }
    }
    $value = ''
    if ($command.StartsWith('resource list')) {
      if ($scenario -notin @('save', 'save-new', 'invalid-bindings')) {
        Assert-True ($script:events.Contains('Next: check custom-domain configuration and Azure-managed certificate status.')) 'Domain setup must be announced before querying Azure.'
      }
      if ($scenario -ne 'save-new') { $value = '/subscriptions/fixture/resourceGroups/rg-domain-test-eastus2/providers/Microsoft.App/containerApps/app' }
      Assert-True ($command.Contains('--subscription 00000000-0000-0000-0000-000000000000')) 'Discovery must explicitly scope subscription.'
      Assert-True ($command.Contains('--tag azd-service-name=mockapi')) 'Discovery must filter by the azd service tag.'
      Assert-True (-not $command.Contains('--resource-type')) 'Discovery must not combine mutually exclusive Azure CLI tag and resource-type filters.'
      Assert-True ($command.Contains("type=='Microsoft.App/containerApps'")) 'Discovery must restrict tagged resources to Container Apps.'
    }
    elseif ($command.Contains('--query properties.configuration.ingress.customDomains')) {
      $value = if ($scenario -eq 'invalid-bindings') { '{}' } else { '[{"name":"old.example.com","bindingType":"SniEnabled","certificateId":"/managedCertificates/old"}]' }
    }
    elseif ($command.StartsWith('containerapp env')) { $value = '192.0.2.1' }
    else {
      $binding = switch ($scenario) {
        existing { '{"name":"api.example.com","bindingType":"SniEnabled","certificateId":"/managedCertificates/cert"}' }
        foreign { '{"name":"api.example.com","bindingType":"SniEnabled","certificateId":"/certificates/cert"}' }
        pending { '{"name":"api.example.com","bindingType":"Disabled","certificateId":null}' }
        default { '' }
      }
      $value = '{"name":"app","properties":{"environmentId":"/environment/id","customDomainVerificationId":"verification","configuration":{"ingress":{"fqdn":"generated.example.test","customDomains":[' + $binding + ']}}}}'
    }
    return @{ ExitCode = 0; Output = @($value) }
  }
  function Invoke-Tool {
    param([string] $Executable, [string[]] $Arguments, [string] $Operation)
    $script:events.Add("MUTATE $Executable $($Arguments -join ' ')")
    if ($scenario -eq 'bind-failure' -and $Arguments[2] -eq 'bind') { throw 'Certificate issuance failed.' }
  }
  function Write-Host {
    param([string] $Object, [ConsoleColor] $ForegroundColor)
    $script:events.Add($Object)
  }
  function Test-AzureDeployment {
    param([string] $EnvironmentName, [string] $ServiceUri = 'https://generated.example.test')
    $script:events.Add("READY $ServiceUri")
    return $ServiceUri
  }

  foreach ($scenario in @(
      'new', 'http', 'existing', 'pending', 'dns-pending-add', 'dns-pending-bind',
      'foreign', 'read-failure', 'add-failure', 'bind-failure', 'empty', 'save', 'save-new', 'invalid-bindings'
    )) {
    $domain = if ($scenario -eq 'empty') { '' } else { 'api.example.com' }
    $method = if ($scenario -eq 'http') { 'HTTP' } else { 'CNAME' }
    $environmentFile = New-EnvironmentFixture ($baseSettings + "AZURE_CUSTOM_DOMAIN=$domain" + "AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=$method")
    $configuration = Get-AzureDeploymentConfiguration
    $script:events = [Collections.Generic.List[string]]::new()
    $failure = $null
    $settingsFile = $environmentFile
    try {
      $EnvironmentFile = "settings with 'quotes'.env"
      if ($scenario -in @('save', 'save-new', 'invalid-bindings')) {
        Save-AzureDomainBindings $configuration
      }
      else { $null = Complete-AzureDeployment $configuration }
    }
    catch { $failure = $_ }
    finally { $environmentFile = $settingsFile }
    $result = Invoke-BashTest $scenario $environmentFile
    $expectedSuccess = $scenario -notin @('foreign', 'read-failure', 'add-failure', 'bind-failure', 'invalid-bindings')
    Assert-True (($null -eq $failure) -eq $expectedSuccess) "PowerShell $scenario failed: $failure"
    Assert-True (($result.ExitCode -eq 0) -eq $expectedSuccess) "Bash $scenario failed: $($result.Output)"
    foreach ($output in @(($script:events -join "`n"), $result.Output)) {
      if ($scenario -in @('new', 'http', 'pending', 'bind-failure')) {
        Assert-True ($output.Contains("--validation-method $method")) "$scenario must bind using $method."
      }
      if ($scenario -in @('new', 'http', 'pending', 'dns-pending-add', 'dns-pending-bind', 'add-failure', 'bind-failure')) {
        Assert-True ($output -match 'DNS TXT\s+: asuid.api.example.com -> verification') 'Ownership TXT record must be printed.'
        $dnsPattern = if ($scenario -eq 'http') { 'DNS A\s+: api.example.com\s+-> 192.0.2.1' } else { 'DNS CNAME\s+: api.example.com\s+-> generated.example.test' }
        Assert-True ($output -match $dnsPattern) 'Correct DNS target must be printed.'
        $dnsLines = @($output -split "`n" | Where-Object { $_ -match '^   DNS (A|CNAME|TXT)\s+:' })
        Assert-True ($dnsLines.Count -eq 2) 'Both DNS routing and ownership rows must be present.'
        $normalizedOutput = $output.Replace("`r", '')
        Assert-True ($normalizedOutput.Contains("no DNS changes are needed.`n`nYellow = action required from you. Cyan = exact DNS entry or command to copy.`n`n1)")) 'Separate the introduction, color legend, and numbered steps with blank lines.'
        Assert-True ([regex]::Matches($normalizedOutput, '(?m)^[1-4]\) [^\n]+\n\n').Count -eq 4) 'Each step heading must have a blank line before its details.'
        Assert-True ([regex]::Matches($output, '(?m)^(1|2|4)\) ACTION REQUIRED:').Count -eq 3) 'Required DNS and continuation actions must be unmistakably labeled.'
        Assert-True ($output.Contains('3) OPTIONAL CHECK:')) 'Conditional CAA guidance must be labeled as an optional check.'
        Assert-True ($output.Contains('Cyan = exact DNS entry or command to copy.')) 'The color legend must explain which values are safe to copy.'
        foreach ($dnsLine in $dnsLines) {
          Assert-True ($normalizedOutput.Contains("`n`n$($dnsLine.TrimEnd("`r"))`n`n   Why:")) 'Indented DNS rows must have a blank line above and below.'
        }
        Assert-True ($dnsLines[0].IndexOf('->') -eq $dnsLines[1].IndexOf('->')) 'DNS arrows must align vertically.'
        $steps = [regex]::Matches($output, '(?m)^[1-4]\) ')
        Assert-True (($steps.Value -join '') -ceq '1) 2) 3) 4) ') 'Instructions must contain four ordered numeric steps.'
        Assert-True ([regex]::Matches($output, '(?m)^   Why:').Count -eq 4) 'Each step must explain why it is necessary.'
        Assert-True ($output.Contains('0 issue "digicert.com"')) 'CAA instructions must give the exact issuer policy.'
        Assert-True ($output.Contains('Do not place CAA at a CNAME name')) 'CAA instructions must avoid conflicting records.'
        Assert-True ($output.Contains('If no CAA policy restricts issuers, no CAA change is required.')) 'CAA must not be presented as unconditionally required.'
        Assert-True ($output.Contains("PowerShell: .\start.ps1 -Action azure-deploy -EnvironmentFile 'settings with ''quotes''.env'")) 'PowerShell retry must quote the original environment file.'
        Assert-True ($output.Contains("Bash:       ./start.sh --action azure-deploy --environment-file 'settings with '\''quotes'\''.env'")) 'Bash retry must quote the original environment file.'
        Assert-True ($output.Contains('automatically adds the custom hostname, issues and binds the certificate')) 'Retry must explain automatic hostname and certificate configuration.'
      }
      if ($scenario -in @('new', 'http', 'pending', 'bind-failure')) {
        Assert-True ($output.Contains('hostname add') -eq ($scenario -ne 'pending')) 'Only existing pending hostnames should skip the add operation.'
      }
      if ($scenario -in @('new', 'http', 'pending', 'existing')) {
        Assert-True ($output.Contains('READY https://api.example.com')) 'Custom HTTPS endpoint must be tested.'
      }
      if ($scenario -in @('empty', 'existing', 'foreign', 'read-failure', 'invalid-bindings')) {
        Assert-True (-not $output.Contains('MUTATE')) "$scenario must not mutate Azure."
      }
      if (-not $expectedSuccess) {
        Assert-True (-not $output.Contains('READY https://api.example.com')) 'Failure must stop before custom-domain readiness.'
      }
      if ($scenario -in @('dns-pending-add', 'dns-pending-bind')) {
        Assert-True ($output.Contains('Pending DNS validation')) 'Missing DNS must be reported as a pending custom domain.'
        Assert-True ($output.Contains('expected until DNS is configured and propagated')) 'Missing DNS must be identified as an expected state.'
        Assert-True (-not $output.Contains('ERROR:')) 'Expected DNS validation diagnostics must not leak as errors.'
        Assert-True (-not $output.Contains('READY https://api.example.com')) 'A pending custom domain must not be tested or reported as ready.'
      }
      if ($scenario -in @('dns-pending-add', 'add-failure')) {
        Assert-True (-not $output.Contains('hostname bind')) 'A failed hostname add must stop certificate binding.'
      }
      if ($scenario -in @('new', 'http', 'pending')) {
        Assert-True ($output.Contains('Hostname operation completed.')) 'Successful Azure output must remain visible.'
      }
      if ($scenario -eq 'bind-failure') {
        Assert-True ($output.Contains('Certificate issuance failed.')) 'Unexpected certificate failures must retain diagnostics.'
      }
      if ($scenario -eq 'add-failure') {
        Assert-True ($output.Contains('Authorization failed.')) 'Unexpected hostname failures must retain diagnostics.'
      }
      if ($scenario -eq 'save') {
        Assert-True ($output.Contains('AZURE_CUSTOM_DOMAINS [{"name":"old.example.com"')) 'Existing unrelated domain bindings must be preserved.'
      }
      if ($scenario -eq 'save-new') {
        Assert-True ($output.Contains('AZURE_CUSTOM_DOMAINS []')) 'New environments must clear stale saved bindings.'
      }
    }
    if ($scenario -in @('new', 'http', 'pending', 'dns-pending-add', 'dns-pending-bind')) {
      $instructionsPattern = '(?s)Custom domain setup:.*?Keep the DNS records and public ingress available for automatic certificate renewal\.'
      $powerShellInstructions = [regex]::Match(($script:events -join "`n"), $instructionsPattern).Value
      $bashInstructions = [regex]::Match($result.Output.Replace("`r", ''), $instructionsPattern).Value
      Assert-True ($powerShellInstructions -ceq $bashInstructions) 'DNS instructions must match exactly across shells.'
    }
  }
  foreach ($action in @('Up', 'Deploy', 'Import')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Invoke-Azure$action"
      }, $true).Extent.Text
    Assert-True ($definition -match '(?s)ShouldProcess.*Complete-AzureDeployment') "$action must configure domains only after confirmation."
  }
  $pushDefinition = $ast.Find({ param($node)
      $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-AzurePush'
    }, $true).Extent.Text
  Assert-True ($pushDefinition -notmatch 'Complete-AzureDeployment|Set-AzureCustomDomain') 'Registry-only push must not configure domains.'
  foreach ($action in @('up', 'deploy', 'import')) {
    $definition = [regex]::Match($bashSource, "(?ms)^invoke_azure_$action\(\) \{.*?^\}").Value
    Assert-True ($definition -match '(?s)should_process.*complete_azure_deployment') "Bash $action must configure domains only after confirmation."
  }
  $pushDefinition = [regex]::Match($bashSource, '(?ms)^invoke_azure_push\(\) \{.*?^\}').Value
  Assert-True ($pushDefinition -notmatch 'complete_azure_deployment|set_azure_custom_domain') 'Bash registry-only push must not configure domains.'
  $parameters = Get-Content (Join-Path $repositoryRoot 'infra/main.parameters.json') -Raw | ConvertFrom-Json
  Assert-True ($parameters.parameters.customDomainsJson.value -ceq '${AZURE_CUSTOM_DOMAINS=[]}') 'Infrastructure must default preserved bindings to an empty JSON array.'

  & {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-AzureDeployment'
      }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
    function Get-AzureServiceUri { throw 'Explicit custom URI must not resolve the default endpoint.' }
    function Invoke-WebRequest {
      param([string] $Uri, [int] $TimeoutSec, [switch] $SkipHttpErrorCheck)
      Assert-True ($Uri -ceq 'https://api.example.com/health/ready') 'Readiness must use the custom HTTPS hostname.'
      return @{ StatusCode = 200; Content = '{"status":"ready"}' }
    }
    $uri = Test-AzureDeployment -EnvironmentName domain-test -ServiceUri 'https://api.example.com'
    Assert-True ($uri -ceq 'https://api.example.com') 'Readiness must return the verified custom URL.'
  }
  & {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-NativeTool'
      }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
    $script:events.Clear()
    $result = Invoke-NativeTool -Executable 'pwsh' -Arguments @(
      '-NoProfile', '-Command', '[Console]::Error.WriteLine("ERROR: (InvalidCustomHostNameValidation) DNS not found."); exit 1'
    ) -CaptureStandardError
    Assert-True ($result.ExitCode -eq 1) 'Capturing stderr must preserve the native exit code.'
    Assert-True (($result.Output -join "`n").Contains('InvalidCustomHostNameValidation')) 'Native stderr must be available for classification.'
    Assert-True ($script:events.Count -eq 0) 'Captured diagnostics must not be streamed to the console.'
  }
  $continuationPowerShellFunctions = foreach ($name in @('Complete-AzureDeployment', 'Read-AzureDomainContinuation', 'Wait-ForMenuReturn')) {
    $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
      }, $true).Extent.Text
  }
  $continuationBashFunctions = foreach ($name in @('complete_azure_deployment', 'read_azure_domain_continuation', 'wait_for_menu_return')) {
    [regex]::Match($bashSource, "(?ms)^$name\(\) \{.*?^\}").Value
  }
  $powerShellContinuation = @'
param([string] $Mode)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Action = $Mode
$script:DomainContinuationPrompted = $false
$script:attempts = 0
function Set-AzureCustomDomain {
  param($Configuration)
  $script:attempts++
  return $script:attempts -ge 3
}
function Test-AzureDeployment {
  param($EnvironmentName, $ServiceUri = 'https://generated.example.test')
  Write-Host "READY $ServiceUri"
  return $ServiceUri
}
function Clear-Host { throw 'Domain continuation must not clear the screen.' }
'@
  $powerShellContinuation += "`n" + ($continuationPowerShellFunctions -join "`n") + "`n" + @'
$uri = Complete-AzureDeployment @{ AZURE_ENV_NAME = 'fixture'; AZURE_CUSTOM_DOMAIN = 'api.example.com' }
if ($Action -eq 'menu') { Wait-ForMenuReturn }
Write-Host "ATTEMPTS $script:attempts"
Write-Host "FINAL $uri"
Write-Host 'MENU CONTEXT RETAINED'
'@
  $bashContinuation = @'
#!/usr/bin/env bash
set -Eeuo pipefail
ACTION="$1"
DOMAIN_CONTINUATION_PROMPTED=false
AZURE_CONFIGURATION_ENVIRONMENT_NAME=fixture
AZURE_CONFIGURATION=''
attempts=0
write_color() { printf '%s\n' "$2"; }
configuration_value() { printf '%s\n' 'api.example.com'; }
set_azure_custom_domain() {
  attempts=$((attempts + 1))
  if (( attempts < 3 )); then AZURE_CUSTOM_DOMAIN_PENDING=true
  else AZURE_CUSTOM_DOMAIN_READY=true; fi
}
test_azure_deployment() {
  AZURE_SERVICE_URI="${2:-https://generated.example.test}"
  printf 'READY %s\n' "$AZURE_SERVICE_URI"
}
clear() { printf 'Unexpected screen clear\n' >&2; exit 99; }
'@
  $bashContinuation += "`n" + ($continuationBashFunctions -join "`n") + "`n" + @'
# Match the menu's subshell lifecycle: the wait must see continuation state.
(
  if [[ "$ACTION" == menu ]]; then trap 'wait_for_menu_return' EXIT; fi
  complete_azure_deployment
  printf 'ATTEMPTS %s\n' "$attempts"
  printf 'FINAL %s\n' "$AZURE_SERVICE_URI"
)
printf 'MENU CONTEXT RETAINED\n'
'@
  $continuationPowerShellPath = Join-Path $testDirectory 'continuation.ps1'
  $continuationBashPath = Join-Path $testDirectory 'continuation.sh'
  Set-Content -LiteralPath $continuationPowerShellPath -Value $powerShellContinuation -Encoding utf8NoBOM
  Set-Content -LiteralPath $continuationBashPath -Value $bashContinuation.Replace("`r", '') -Encoding utf8NoBOM
  foreach ($case in @(
      @{ Mode = 'menu'; Input = 'x'; Attempts = 1; Ready = $false },
      @{ Mode = 'menu'; Input = "4`nx"; Attempts = 2; Ready = $false },
      @{ Mode = 'menu'; Input = "4`n4"; Attempts = 3; Ready = $true },
      @{ Mode = 'menu'; Input = ''; Attempts = 1; Ready = $false },
      @{ Mode = 'azure-deploy'; Input = '4'; Attempts = 1; Ready = $false }
    )) {
    $powerShellOutput = $case.Input | & pwsh -NoProfile -File $continuationPowerShellPath $case.Mode 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "PowerShell continuation failed: $powerShellOutput"
    $relativeDirectory = Split-Path -Leaf $testDirectory
    $bashOutput = $case.Input | & bash "./$relativeDirectory/continuation.sh" $case.Mode 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "Bash continuation failed: $bashOutput"
    foreach ($output in @(($powerShellOutput -join "`n"), ($bashOutput -join "`n"))) {
      Assert-True ($output.Contains("ATTEMPTS $($case.Attempts)")) "Only pressing 4 should retry pending domain setup. Expected $($case.Attempts) attempts for $($case.Mode): $output"
      Assert-True ($output.Contains('READY https://api.example.com') -eq $case.Ready) 'Only a bound certificate should trigger custom HTTPS verification.'
      Assert-True ([regex]::Matches($output, 'READY https://generated.example.test').Count -eq 1) 'Retries must not repeat the initial deployment readiness phase.'
      Assert-True (-not $output.Contains('Press any key to return to the menu.')) 'Domain continuation must not require a second menu-return keypress.'
      Assert-True ($output.Contains('MENU CONTEXT RETAINED')) 'Returning must preserve menu context.'
      Assert-True ($output.Contains('Press 4 to retry domain setup') -eq ($case.Mode -eq 'menu')) 'Command-line actions must remain non-interactive.'
    }
  }
  Microsoft.PowerShell.Utility\Write-Host 'Custom-domain CLI tests passed in PowerShell and Bash.'
}
finally {
  if (Test-Path variable:savedEnvironment) {
    foreach ($key in $savedEnvironment.Keys) {
      [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key])
    }
  }
  Remove-Item -LiteralPath $testDirectory -Recurse -Force
}
