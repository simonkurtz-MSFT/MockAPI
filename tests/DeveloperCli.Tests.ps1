#!/usr/bin/env pwsh
[CmdletBinding()]
param(
  [switch] $MenuOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cliPath = Join-Path $repositoryRoot 'start.ps1'
$bashCliPath = Join-Path $repositoryRoot 'start.sh'
$dockerfilePath = Join-Path $repositoryRoot 'Dockerfile'
$azureYamlPath = Join-Path $repositoryRoot 'azure.yaml'
$globalJsonPath = Join-Path $repositoryRoot 'global.json'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "mockapi-cli-$([Guid]::NewGuid().ToString('N'))"
$stubDirectory = Join-Path $testRoot 'bin'
$commandLog = Join-Path $testRoot 'commands.log'
$loginMarker = Join-Path $testRoot 'authenticated'
$environmentMarker = Join-Path $testRoot 'environment-created'
$bashEnvironmentPath = Join-Path $repositoryRoot ".developer-cli-bash-$([Guid]::NewGuid().ToString('N')).env"
$bashMenuTestPath = Join-Path $repositoryRoot ".developer-cli-bash-menu-$([Guid]::NewGuid().ToString('N')).sh"

function Assert-True {
  param(
    [Parameter(Mandatory)][bool] $Condition,
    [Parameter(Mandatory)][string] $Message
  )

  if (-not $Condition) {
    throw $Message
  }
}

function Invoke-CliProcess {
  param(
    [Parameter(Mandatory)][string[]] $Arguments,
    [hashtable] $Environment = @{}
  )

  $process = [Diagnostics.Process]::new()
  $process.StartInfo.FileName = (Get-Command pwsh).Source
  $process.StartInfo.WorkingDirectory = $repositoryRoot
  $process.StartInfo.UseShellExecute = $false
  $process.StartInfo.RedirectStandardOutput = $true
  $process.StartInfo.RedirectStandardError = $true
  $process.StartInfo.ArgumentList.Add('-NoProfile')
  $process.StartInfo.ArgumentList.Add('-File')
  $process.StartInfo.ArgumentList.Add($cliPath)
  foreach ($argument in $Arguments) {
    $process.StartInfo.ArgumentList.Add($argument)
  }
  foreach ($entry in $Environment.GetEnumerator()) {
    $process.StartInfo.Environment[[string] $entry.Key] = [string] $entry.Value
  }
  $process.StartInfo.Environment['PATH'] = "$stubDirectory$([IO.Path]::PathSeparator)$env:PATH"
  $process.StartInfo.Environment['MOCKAPI_TEST_COMMAND_LOG'] = $commandLog
  $process.StartInfo.Environment['MOCKAPI_TEST_LOGIN_MARKER'] = $loginMarker
  $process.StartInfo.Environment['MOCKAPI_TEST_ENVIRONMENT_MARKER'] = $environmentMarker

  $null = $process.Start()
  $standardOutput = $process.StandardOutput.ReadToEnd()
  $standardError = $process.StandardError.ReadToEnd()
  $process.WaitForExit()
  return [pscustomobject]@{
    ExitCode = $process.ExitCode
    Output = "$standardOutput`n$standardError"
  }
}

function Invoke-BashCliProcess {
  param(
    [Parameter(Mandatory)][AllowEmptyString()][string[]] $Arguments,
    [string] $ScriptPath = './start.sh'
  )

  $process = [Diagnostics.Process]::new()
  $process.StartInfo.FileName = (Get-Command bash).Source
  $process.StartInfo.WorkingDirectory = $repositoryRoot
  $process.StartInfo.UseShellExecute = $false
  $process.StartInfo.RedirectStandardOutput = $true
  $process.StartInfo.RedirectStandardError = $true
  $process.StartInfo.ArgumentList.Add($ScriptPath)
  foreach ($argument in $Arguments) {
    $process.StartInfo.ArgumentList.Add($argument)
  }

  $null = $process.Start()
  $standardOutput = $process.StandardOutput.ReadToEnd()
  $standardError = $process.StandardError.ReadToEnd()
  $process.WaitForExit()
  return [pscustomobject]@{
    ExitCode = $process.ExitCode
    Output = "$standardOutput`n$standardError"
  }
}

function New-EnvironmentFile {
  param(
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string[]] $Lines
  )

  $path = Join-Path $testRoot $Name
  Set-Content -LiteralPath $path -Value $Lines
  return $path
}

function Import-CliFunction {
  param([Parameter(Mandatory)][string] $Name)

  $tokens = $null
  $errors = $null
  $ast = [Management.Automation.Language.Parser]::ParseFile(
    $cliPath,
    [ref] $tokens,
    [ref] $errors
  )
  Assert-True ($errors.Count -eq 0) "Developer CLI has parse errors: $errors"
  $definitions = @($ast.FindAll({
      param($node)
      $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
    }, $true))
  Assert-True ($definitions.Count -eq 1) "Expected exactly one '$Name' function definition."
  return [scriptblock]::Create($definitions[0].Extent.Text)
}

function Test-LocalRun {
  . (Import-CliFunction -Name 'Get-LocalApplicationUri')
  . (Import-CliFunction -Name 'Invoke-Run')
  . (Import-CliFunction -Name 'Invoke-Action')
  function Assert-DotNet {}
  function Write-Host {}
  function Start-BrowserWhenReady {
    param([string] $ApplicationUri)
    $script:localRunUri = $ApplicationUri
    return [pscustomobject]@{ State = 'Running' }
  }
  function Invoke-NativeTool {
    param([string] $Executable, [string[]] $Arguments, [switch] $StreamOutput)
    Assert-True ($Executable -eq 'dotnet' -and $Arguments[0] -eq 'run' -and $StreamOutput) 'Local run must stream dotnet run output.'
    return [pscustomobject]@{ ExitCode = $script:localRunExitCode }
  }
  function Stop-Job { param($Job) $script:localRunCleanup.Add('stop') }
  function Receive-Job { param($Job) $script:localRunCleanup.Add('receive') }
  function Remove-Job { param($Job, [switch] $Force) $script:localRunCleanup.Add('remove') }

  $bashSource = Get-Content -LiteralPath $bashCliPath -Raw
  $bashFunctions = foreach ($name in @('get_local_application_url', 'open_browser_when_ready', 'invoke_run', 'invoke_action')) {
    $definition = [regex]::Match($bashSource, "(?ms)^$([regex]::Escape($name))\(\) \{.*?^\}")
    Assert-True $definition.Success "Bash must define $name."
    $definition.Value
  }
  $bashHarness = @'
#!/usr/bin/env bash
set -Eeuo pipefail
ASPNETCORE_URLS="$(printf '%s' "${1#urls:}" | base64 --decode)"
selectedAction="$2"
runExitCode="$3"
REPOSITORY_ROOT='.'
CONFIGURATION='Debug'
browserLog="$(mktemp)"
trap 'rm -f "$browserLog"' EXIT
assert_dotnet() { :; }
require_command() { return 0; }
write_color() { printf '%s\n' "$2"; }
die() { printf '%s\n' "$1" >&2; exit 1; }
curl() { printf '200'; }
open_local_browser() { printf '%s\n' "$1" > "$browserLog"; }
dotnet() {
  [[ "$1" == 'run' ]] || return 98
  (( runExitCode == 0 )) || return "$runExitCode"
  for ((attempt=0; attempt<100; attempt++)); do
    [[ -s "$browserLog" ]] && return 0
    sleep 0.01
  done
  return 99
}
'@
  $bashTest = $bashHarness + "`n" + ($bashFunctions -join "`n") + "`n" + @'
invoke_action "$selectedAction"
printf 'BROWSER_URL=%s\n' "$(cat "$browserLog")"
'@
  Set-Content -LiteralPath $bashMenuTestPath -Value $bashTest.Replace("`r", '') -Encoding utf8NoBOM -NoNewline
  $bashScript = './' + (Split-Path -Leaf $bashMenuTestPath)
  $originalUrls = $env:ASPNETCORE_URLS
  $Configuration = 'Debug'
  $projectFile = Join-Path $repositoryRoot 'src/MockAPI/MockAPI.csproj'
  $script:localRunExitCode = 0
  $script:localRunCleanup = [Collections.Generic.List[string]]::new()
  try {
    foreach ($scenario in @(
        @{ Urls = ''; Base = 'http://localhost:5000/' },
        @{ Urls = 'http://localhost:5080'; Base = 'http://localhost:5080/' },
        @{ Urls = 'http://0.0.0.0:5080'; Base = 'http://localhost:5080/' },
        @{ Urls = 'http://[::]:5080'; Base = 'http://localhost:5080/' },
        @{ Urls = 'http://*:5080'; Base = 'http://localhost:5080/' },
        @{ Urls = 'https://+:7080;http://localhost:5080'; Base = 'https://localhost:7080/' }
      )) {
      $env:ASPNETCORE_URLS = $scenario.Urls
      foreach ($action in @('run', 'run-tutorial')) {
        $script:localRunCleanup.Clear()
        Invoke-Action -SelectedAction $action
        $mode = if ($action -eq 'run') { 'skip' } else { 'show' }
        $expectedUri = "$($scenario.Base)?tutorial=$mode"
        Assert-True ($script:localRunUri -ceq $expectedUri) "$action must open the correct tutorial URL."
        Assert-True (($script:localRunCleanup -join ',') -eq 'stop,receive,remove') 'Local run must clean up its browser worker.'
        $encodedUrls = 'urls:' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($scenario.Urls))
        $bashResult = Invoke-BashCliProcess -ScriptPath $bashScript -Arguments @($encodedUrls, $action, '0')
        Assert-True ($bashResult.ExitCode -eq 0) "Bash local run failed: $($bashResult.Output)"
        Assert-True ($bashResult.Output.Contains("BROWSER_URL=$expectedUri")) 'Both shells must open the same tutorial URL.'
      }
    }

    $script:localRunExitCode = 7
    $script:localRunCleanup.Clear()
    $failed = $false
    try { Invoke-Run } catch { $failed = $_.Exception.Message -eq 'Running MockAPI failed with exit code 7.' }
    Assert-True $failed 'A failed local run must report the dotnet exit code.'
    Assert-True (($script:localRunCleanup -join ',') -eq 'stop,receive,remove') 'A failed run must also clean up the browser worker.'
    $bashFailure = Invoke-BashCliProcess -ScriptPath $bashScript -Arguments @('urls:', 'run', '7')
    Assert-True ($bashFailure.ExitCode -ne 0 -and $bashFailure.Output.Contains('Running MockAPI failed with exit code 7.')) 'Bash must report launch failure and exit its worker.'
  }
  finally {
    $env:ASPNETCORE_URLS = $originalUrls
  }
  Microsoft.PowerShell.Utility\Write-Host 'Developer CLI local launch tests passed (tutorial modes, URL parity, and worker cleanup).'
}

function Test-SitePreview {
  . (Import-CliFunction -Name 'Invoke-SitePreview')
  . (Import-CliFunction -Name 'Invoke-Action')
  . (Import-CliFunction -Name 'Invoke-Tool')
  function Write-Host {}
  function Invoke-NativeTool {
    param([string] $Executable, [string[]] $Arguments, [switch] $StreamOutput)
    Assert-True ($Executable -eq 'node' -and $StreamOutput) 'Site preview must stream the Node server output.'
    Assert-True ($Arguments[0] -eq (Join-Path $repositoryRoot 'scripts/serve-site.cjs')) 'Site preview must use the allowlisted preview server.'
    Assert-True ($Arguments.Count -eq 2 -and $Arguments[1] -eq '--open') 'Site preview must open the browser after readiness.'
    return [pscustomobject]@{ ExitCode = $script:sitePreviewExitCode }
  }

  $script:sitePreviewExitCode = 0
  Invoke-Action -SelectedAction 'site-preview'
  $script:sitePreviewExitCode = 7
  $failed = $false
  try { Invoke-Action -SelectedAction 'site-preview' }
  catch { $failed = $_.Exception.Message -eq 'Previewing the documentation site failed with exit code 7.' }
  Assert-True $failed 'Site preview must report server failures.'

  $bashSource = Get-Content -LiteralPath $bashCliPath -Raw
  $bashFunctions = foreach ($name in @('invoke_site_preview', 'invoke_action', 'run_tool')) {
    $definition = [regex]::Match($bashSource, "(?ms)^$([regex]::Escape($name))\(\) \{.*?^\}")
    Assert-True $definition.Success "Bash must define $name."
    $definition.Value
  }
  $bashHarness = @'
#!/usr/bin/env bash
set -Eeuo pipefail
REPOSITORY_ROOT='.'
require_command() { [[ "$1" == 'node' ]]; }
write_color() { printf '%s\n' "$2"; }
die() { printf '%s\n' "$1" >&2; exit 1; }
node() {
  [[ "$#" == 2 && "$1" == './scripts/serve-site.cjs' && "$2" == '--open' ]] || return 98
  return "$previewExitCode"
}
previewExitCode="$1"
'@
  $bashTest = $bashHarness + "`n" + ($bashFunctions -join "`n") + "`ninvoke_action site-preview`n"
  Set-Content -LiteralPath $bashMenuTestPath -Value $bashTest.Replace("`r", '') -Encoding utf8NoBOM -NoNewline
  $bashScript = './' + (Split-Path -Leaf $bashMenuTestPath)
  $success = Invoke-BashCliProcess -ScriptPath $bashScript -Arguments @('0')
  Assert-True ($success.ExitCode -eq 0) "Bash site preview failed: $($success.Output)"
  $failure = Invoke-BashCliProcess -ScriptPath $bashScript -Arguments @('7')
  Assert-True ($failure.ExitCode -ne 0 -and $failure.Output.Contains('Previewing the documentation site failed with exit code 7.')) 'Bash must report the same server failure.'
  Microsoft.PowerShell.Utility\Write-Host 'Developer CLI site preview tests passed (server arguments, browser opening, and failure parity).'
}

function Test-InteractiveMenu {
  . (Import-CliFunction -Name 'Show-Menu')
  . (Import-CliFunction -Name 'Show-SubmenuHelp')
  $script:readHostResponses = [Collections.Generic.Queue[object]]::new()
  $script:menuLines = [Collections.Generic.List[string]]::new()
  $script:resetConsoleColorCount = 0
  function Reset-ConsoleColors {
    $script:resetConsoleColorCount++
  }
  function Read-Host {
    param([string] $Prompt)
    return $script:readHostResponses.Dequeue()
  }
  function Write-Host {
    param(
      [Parameter(Position = 0, ValueFromRemainingArguments)][object[]] $Object,
      [ConsoleColor] $ForegroundColor
    )
    $script:menuLines.Add(($Object -join ' '))
  }

  $bashSource = Get-Content -LiteralPath $bashCliPath -Raw
  $bashMenu = [regex]::Match($bashSource, '(?ms)^show_menu\(\) \{.*?^\}')
  Assert-True $bashMenu.Success 'Bash must define show_menu.'
  $powerShellSource = Get-Content -LiteralPath $cliPath -Raw
  $actionValidation = [regex]::Match($powerShellSource, '(?s)\[ValidateSet\((.*?)\)\]\s*\[string\] \$Action')
  $powerShellActions = @([regex]::Matches($actionValidation.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
  $bashActions = @([regex]::Match($bashSource, '(?m)^VALID_ACTIONS="([^"]+)"').Groups[1].Value.Split(' ') | Sort-Object)
  Assert-True (($powerShellActions -join ',') -ceq ($bashActions -join ',')) 'Both CLI entry points must accept identical action lists.'
  $bashHarness = @'
#!/usr/bin/env bash
set -Eeuo pipefail
CONTAINER_ENGINE="$1"
shift
MENU_CONTEXT="${1#menu:}"
shift
reset_console_colors() { :; }
write_color() { printf '%s\n' "$2"; }
'@
  $bashTest = $bashHarness + "`n" + $bashMenu.Value + @'

show_menu < <(printf '%s\n' "$@")
printf 'SELECTED_ACTION=%s\n' "$MENU_SELECTION"
'@
  Set-Content -LiteralPath $bashMenuTestPath -Value $bashTest.Replace("`r", '') -Encoding utf8NoBOM -NoNewline
  $bashMenuScript = './' + (Split-Path -Leaf $bashMenuTestPath)

  $actions = [ordered]@{
    l1 = 'run'; h = 'help'; q = 'quit'
    v1 = 'validate'; v2 = 'test'
    p1 = 'pathway-azure-initial'; p2 = 'pathway-azure-update'
    a1 = 'azure-setup'; a2 = 'azure-check'; a3 = 'azure-up'
    a4 = 'azure-deploy'; a5 = 'azure-push'; a6 = 'azure-import'; a7 = 'azure-down'
    c1 = 'container-engine-wslc'; c2 = 'container-engine-docker'
    c3 = 'container-build'; c4 = 'container-run'; c5 = 'container-test'
    c6 = 'container-showcase'; c7 = 'container-logs'; c8 = 'container-status'
    c9 = 'container-stop'; c10 = 'container-remove'
    s1 = 'setup'; s2 = 'dependency-tooling-update'; s3 = 'pnpm-update'
  }
  $menuKeys = @{
    Home = @('l', 'v', 'a', 'c', 's', 'h', 'q')
    'Run locally' = @('1', '2', '3', 'b', 'h', 'q')
    Verify = @('1', '2', 'b', 'h', 'q')
    Azure = @('1', '2', '3', '4', '5', '6', '7', '8', '9', 'b', 'h', 'q')
    Containers = @('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', 'b', 'h', 'q')
    Setup = @('1', '2', 'b', 'h', 'q')
  }
  $menuActions = @{
    'Run locally' = @('run', 'run-tutorial', 'site-preview')
    Verify = @('validate', 'test')
    Azure = @(
      'pathway-azure-initial', 'pathway-azure-update', 'azure-setup', 'azure-check', 'azure-up',
      'azure-deploy', 'azure-push', 'azure-import', 'azure-down'
    )
    Containers = @(
      'container-engine-wslc', 'container-engine-docker', 'container-build', 'container-run', 'container-test',
      'container-showcase', 'container-logs', 'container-status', 'container-stop', 'container-remove'
    )
    Setup = @('setup', 'dependency-tooling-update')
  }
  $groups = [ordered]@{ l = 'Run locally'; v = 'Verify'; a = 'Azure'; c = 'Containers'; s = 'Setup' }
  $scenarios = [Collections.Generic.List[object]]::new()
  foreach ($key in $actions.Keys) {
    $scenarios.Add(@{
        Name = "Home shortcut $key"; Input = @($key); Action = $actions[$key]; Menus = @('Home')
      })
  }
  foreach ($group in $groups.Keys) {
    $menu = $groups[$group]
    foreach ($key in $menuKeys[$menu] | Where-Object { $_ -notin @('b', 'h', 'q') }) {
      $expectedAction = $menuActions[$menu][[int] $key - 1]
      $scenarios.Add(@{
          Name = "$menu shortcut $key"; Input = @($group, $key); Action = $expectedAction; Menus = @('Home', $menu)
        })
    }
    foreach ($key in @('h', 'q')) {
      $expectedAction = if ($key -eq 'h') { "help-$($menu.ToLowerInvariant().Replace(' ', '-'))" } else { 'quit' }
      $scenarios.Add(@{
          Name = "$menu shortcut $key"; Input = @($group, $key); Action = $expectedAction; Menus = @('Home', $menu)
        })
    }
    $scenarios.Add(@{
        Name = "$menu back"; Input = @($group, 'b', 'q'); Action = 'quit'; Menus = @('Home', $menu, 'Home')
      })
  }
  $scenarios.Add(@{
      Name = 'Cross-group shortcut'; Input = @('s', 'a4'); Action = 'azure-deploy'; Menus = @('Home', 'Setup')
    })
  $scenarios.Add(@{
      Name = 'Contextual numeric shortcut'; Input = @('v', '2'); Action = 'test'; Menus = @('Home', 'Verify')
    })
  $scenarios.Add(@{
      Name = 'Global prefixed shortcut from another submenu'; Input = @('v', 'c3'); Action = 'container-build'; Menus = @('Home', 'Verify')
    })
  $scenarios.Add(@{
      Name = 'Uppercase navigation'; Input = @('C', 'B', 'A', 'P2'); Action = 'pathway-azure-update'
      Menus = @('Home', 'Containers', 'Home', 'Azure')
    })
  $scenarios.Add(@{
      Name = 'Invalid selections stay in menu'; Input = @('invalid', '', 'v', 'invalid', 'b', 'b', 'q'); Action = 'quit'
      Menus = @('Home', 'Home', 'Home', 'Verify', 'Verify', 'Home', 'Home')
    })

  foreach ($scenario in $scenarios) {
    $ContainerEngine = 'wslc'
    $script:menuLines.Clear()
    $script:resetConsoleColorCount = 0
    foreach ($selection in $scenario.Input) {
      $script:readHostResponses.Enqueue($selection)
    }
    Assert-True ((Show-Menu) -eq $scenario.Action) "$($scenario.Name): PowerShell returned the wrong action."
    Assert-True ($script:resetConsoleColorCount -eq $scenario.Menus.Count) "$($scenario.Name): PowerShell must reset console colors before every menu render."
    Assert-True ($script:readHostResponses.Count -eq 0) "$($scenario.Name): PowerShell did not consume all selections."
    Assert-True ($script:menuLines[$script:menuLines.Count - 1] -eq '') 'The menu must leave a blank line below the selection.'
    $menuText = $script:menuLines -join "`n"
    $bashResult = Invoke-BashCliProcess -ScriptPath $bashMenuScript -Arguments (@('wslc', 'menu:Home') + $scenario.Input)
    Assert-True ($bashResult.ExitCode -eq 0) "$($scenario.Name): Bash failed: $($bashResult.Output)"
    $bashOutput = $bashResult.Output.Replace("`r", '')
    Assert-True ($bashOutput.Contains("SELECTED_ACTION=$($scenario.Action)`n")) "$($scenario.Name): Bash returned the wrong action."
    $bashMenuText = ($bashOutput -split 'SELECTED_ACTION=')[0]
    Assert-True ($menuText.TrimEnd() -ceq $bashMenuText.TrimEnd()) "$($scenario.Name): shell menus differ."

    $screens = @($menuText -split 'MockAPI Developer CLI' | Select-Object -Skip 1)
    Assert-True ($screens.Count -eq $scenario.Menus.Count) "$($scenario.Name): wrong number of menu screens."
    for ($index = 0; $index -lt $screens.Count; $index++) {
      $expectedMenu = $scenario.Menus[$index]
      Assert-True ($screens[$index].Contains("`n$expectedMenu`n`n   [")) "$($scenario.Name): missing $expectedMenu heading or spacing."
      if ($expectedMenu -eq 'Home') {
        Assert-True ($screens[$index] -match '(?m)^   \[s\]\s+Setup\n\n   \[h\]') 'The home menu must leave a blank line between Setup and Help.'
      }
      $keys = @([regex]::Matches($screens[$index], '(?m)^   \[([^\]]+)\]') | ForEach-Object { $_.Groups[1].Value })
      Assert-True (($keys -join ',') -ceq ($menuKeys[$expectedMenu] -join ',')) "$($scenario.Name): incorrect $expectedMenu choices or ordering."
      foreach ($option in [regex]::Matches($screens[$index], '(?m)^(   \[[^\]]+\]\s+)\S')) {
        Assert-True ($option.Groups[1].Length -eq 10) 'Menu labels must align at column 11.'
      }
    }
    if ($scenario.Name -eq 'Invalid selections stay in menu') {
      Assert-True (([regex]::Matches($menuText, 'Unknown menu selection')).Count -eq 4) 'Invalid selections must produce explicit errors.'
    }
  }

  $script:menuLines.Clear()
  $script:readHostResponses.Enqueue('q')
  Assert-True ((Show-Menu -InitialMenu Verify) -eq 'quit') 'PowerShell must reopen the submenu that launched the previous action.'
  $reopenedPowerShellMenu = $script:menuLines -join "`n"
  Assert-True (
    $reopenedPowerShellMenu -match '(?m)^Verify$' -and
    $reopenedPowerShellMenu -notmatch '(?m)^Home$'
  ) 'PowerShell must render the preserved submenu instead of Home.'

  $reopenedBashMenu = Invoke-BashCliProcess -ScriptPath $bashMenuScript -Arguments @('wslc', 'menu:Verify', 'q')
  Assert-True ($reopenedBashMenu.ExitCode -eq 0) "Bash preserved-submenu test failed: $($reopenedBashMenu.Output)"
  Assert-True (
    $reopenedBashMenu.Output -match '(?m)^Verify$' -and
    $reopenedBashMenu.Output -notmatch '(?m)^Home$' -and
    $reopenedBashMenu.Output -match '(?m)^SELECTED_ACTION=quit$'
  ) 'Bash must render the preserved submenu instead of Home.'

  Assert-True (
    $powerShellSource -match '(?s)\$menuContext = ''Home''.*?Show-Menu -InitialMenu \$menuContext.*?\$menuContext = \$script:MenuContext'
  ) 'The PowerShell action loop must preserve the selected submenu.'
  Assert-True (
    $bashSource -match "(?s)MENU_CONTEXT='Home'.*?while true; do\s+show_menu"
  ) 'The Bash action loop must preserve the selected submenu.'

  $ContainerEngine = 'docker'
  $script:menuLines.Clear()
  $script:readHostResponses.Enqueue('c')
  $script:readHostResponses.Enqueue('3')
  Assert-True ((Show-Menu) -eq 'container-build') 'Docker selection must preserve the build action.'
  $dockerMenu = $script:menuLines -join "`n"
  Assert-True ($dockerMenu.Contains('Build Dockerfile (docker)')) 'The container submenu must show the selected engine.'
  $bashDocker = Invoke-BashCliProcess -ScriptPath $bashMenuScript -Arguments @('docker', 'menu:Home', 'c', '3')
  Assert-True ($bashDocker.ExitCode -eq 0) "Bash Docker menu failed: $($bashDocker.Output)"
  Assert-True ($dockerMenu.TrimEnd() -ceq (($bashDocker.Output.Replace("`r", '') -split 'SELECTED_ACTION=')[0]).TrimEnd()) 'Docker menu labels must match across shells.'

  $submenuLabels = [ordered]@{
    'Run locally' = @('Run without tutorial', 'Run with tutorial', 'Preview documentation site')
    Verify = @('Validate managed code', 'Run unit tests')
    Azure = @(
      'Test, build, and deploy initial Azure environment',
      'Test, build, and update existing Azure deployment',
      'Set up Azure Tooling', 'Validate deployment configuration', 'Provision and deploy',
      'Build, push, and deploy image', 'Build and push image only',
      'Import Docker Hub image and deploy', 'Delete Azure resources'
    )
    Containers = @(
      'Use WSLC', 'Use Docker', 'Build Dockerfile', 'Start or restart native container',
      'Test running container', 'Load and showcase built-in example', 'Show container logs',
      'Show container status', 'Stop container', 'Remove container'
    )
    Setup = @('Setup local dependencies', 'Update dependencies and pnpm meeting the cooldown')
  }
  $bashSubmenuHelp = [regex]::Match($bashSource, '(?ms)^show_submenu_help\(\) \{.*?^\}')
  Assert-True $bashSubmenuHelp.Success 'Bash must define show_submenu_help.'
  $bashSubmenuHelpTest = @'
#!/usr/bin/env bash
set -Eeuo pipefail
CONTAINER_ENGINE="$1"
shift
die() { printf '%s\n' "$1" >&2; exit 1; }
'@ + "`n" + $bashSubmenuHelp.Value + @'

show_submenu_help "$1"
'@
  Set-Content -LiteralPath $bashMenuTestPath -Value $bashSubmenuHelpTest.Replace("`r", '') -Encoding utf8NoBOM -NoNewline
  $allSubmenuLabels = @($submenuLabels.Values | ForEach-Object { $_ })
  foreach ($section in $submenuLabels.Keys) {
    $ContainerEngine = 'wslc'
    $script:menuLines.Clear()
    Show-SubmenuHelp -Section $section
    $powerShellSubmenuHelp = ($script:menuLines -join "`n").Trim()
    $bashSubmenuResult = Invoke-BashCliProcess -ScriptPath $bashMenuScript -Arguments @('wslc', $section)
    Assert-True ($bashSubmenuResult.ExitCode -eq 0) "$section Bash help failed: $($bashSubmenuResult.Output)"
    $normalizedBashSubmenuHelp = $bashSubmenuResult.Output.Replace("`r", '').Trim()
    Assert-True ($powerShellSubmenuHelp -ceq $normalizedBashSubmenuHelp) "$section submenu help must match across shells."
    foreach ($label in $submenuLabels[$section]) {
      Assert-True ($powerShellSubmenuHelp.Contains($label)) "$section submenu help must include '$label'."
    }
    foreach ($label in $allSubmenuLabels | Where-Object { $_ -notin $submenuLabels[$section] }) {
      Assert-True (-not $powerShellSubmenuHelp.Contains($label)) "$section submenu help must exclude unrelated action '$label'."
    }
  }

  $powershellHelp = Invoke-CliProcess -Arguments @('-Action', 'help')
  $bashHelp = Invoke-BashCliProcess -Arguments @('--action', 'help')
  Assert-True ($powershellHelp.ExitCode -eq 0 -and $bashHelp.ExitCode -eq 0) 'Both help actions must succeed.'
  $menuHelpPattern = '(?s)Interactive menu \(no action required\):.*?(?=\r?\n\r?\n)'
  $powershellMenuHelp = [regex]::Match($powershellHelp.Output, $menuHelpPattern)
  $bashMenuHelp = [regex]::Match($bashHelp.Output, $menuHelpPattern)
  Assert-True ($powershellMenuHelp.Success -and $bashMenuHelp.Success) 'Both help surfaces must describe menu navigation.'
  Assert-True ($powershellMenuHelp.Value.Replace("`r", '') -ceq $bashMenuHelp.Value.Replace("`r", '')) 'Interactive menu help must match.'
  $normalizedPowerShellHelp = $powershellHelp.Output.Replace("`r", '')
  $normalizedBashHelp = $bashHelp.Output.Replace("`r", '')
  Assert-True ($normalizedPowerShellHelp -notmatch '\[l1\]') 'The previous l1 menu shortcut must not remain in PowerShell output.'
  Assert-True ($normalizedBashHelp -notmatch '\[l1\]') 'The previous l1 menu shortcut must not remain in Bash output.'
  foreach ($helpSection in @(
      '1) Pathways', '2) Setup', '3) Local Development', '4) Verification',
      '5) Container Operations (WSLC or Docker)', '6) Azure (Container Apps via azd)',
      '7) Misc'
    )) {
    $formattedHeader = "`n`n`n$helpSection`n`n   "
    Assert-True ($normalizedPowerShellHelp.Contains($formattedHeader)) "PowerShell help section '$helpSection' must have two blank lines above and one below."
    Assert-True ($normalizedBashHelp.Contains($formattedHeader)) "Bash help section '$helpSection' must have two blank lines above and one below."
  }
  Microsoft.PowerShell.Utility\Write-Host "Developer CLI menu tests passed ($($scenarios.Count + 1) scenarios per shell, plus help parity)."
}

try {
  Test-InteractiveMenu
  Test-LocalRun
  Test-SitePreview
  if ($MenuOnly) {
    return
  }

  & (Join-Path $PSScriptRoot 'DeveloperCli.CustomDomains.Tests.ps1')

  $azureYaml = Get-Content -LiteralPath $azureYamlPath -Raw
  Assert-True ($azureYaml -match '(?m)^\s+language:\s+docker\s*$') 'The azd service must use the root Docker workflow without .NET project discovery.'
  $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
  Assert-True ($globalJson.sdk.version -eq '10.0.100') 'The .NET SDK baseline must be the first stable .NET 10 SDK.'
  Assert-True ($globalJson.sdk.rollForward -eq 'latestFeature') 'The .NET SDK must roll forward to the latest installed .NET 10 feature band.'
  Assert-True ($globalJson.sdk.allowPrerelease -eq $false) 'The .NET SDK resolver must exclude prerelease SDKs.'
  $projectSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/MockAPI/MockAPI.csproj') -Raw
  $applicationVersionMatch = [regex]::Match($projectSource, '<Version>([^<]+)</Version>')
  Assert-True $applicationVersionMatch.Success 'The application project must define its version.'
  $escapedApplicationVersion = [regex]::Escape($applicationVersionMatch.Groups[1].Value)
  $cliSource = Get-Content -LiteralPath $cliPath -Raw
  $bashCliSource = Get-Content -LiteralPath $bashCliPath -Raw
  Assert-True (
    $cliSource -match "Write-Host 'Running MockAPI' -ForegroundColor Cyan\r?\n\s+Write-Host ''"
  ) 'PowerShell run output must include a blank line below the Running MockAPI heading.'
  Assert-True (
    $bashCliSource -match "write_color cyan 'Running MockAPI'\r?\n\s+printf '\\n'"
  ) 'Bash run output must include a blank line below the Running MockAPI heading.'
  Assert-True (
    $cliSource -match 'Start-BrowserWhenReady -ApplicationUri'
  ) 'PowerShell run must schedule the browser to open when MockAPI is ready.'
  Assert-True (
    $bashCliSource -match 'open_browser_when_ready "\$applicationUrl" &'
  ) 'Bash run must schedule the browser to open when MockAPI is ready.'
  $dockerfileSource = Get-Content -LiteralPath $dockerfilePath -Raw
  Assert-True ($cliSource -match '\$resolvedVersion -notmatch ''\^10\\\.''') 'The PowerShell CLI must accept the .NET 10 SDK selected by global.json.'
  Assert-True ($bashCliSource -match '\[\[ "\$resolvedVersion" =~ \^10\\\. \]\]') 'The Bash CLI must accept the .NET 10 SDK selected by global.json.'
  Assert-True ($cliSource -notmatch 'PinnedSdkVersion') 'The PowerShell CLI must not restore an exact SDK pin.'
  Assert-True ($bashCliSource -notmatch 'pinned_sdk_version') 'The Bash CLI must not restore an exact SDK pin.'
  Assert-True ($cliSource -match '\[Console\]::OutputEncoding = \[Text\.UTF8Encoding\]::new\(\$false\)') 'The PowerShell CLI must decode native tool output as BOM-less UTF-8.'
  Assert-True ($cliSource -match '\[Console\]::OutputEncoding = \$originalConsoleOutputEncoding') 'The PowerShell CLI must restore the caller console encoding.'
  Assert-True ($cliSource -match "(?s)function Invoke-Validation.*?Invoke-Restore\s+Invoke-DependencySecurity\s+Invoke-Lint") 'The PowerShell validation action must scan restored dependencies before linting.'
  Assert-True ($bashCliSource -match '(?s)invoke_validation\(\).*?invoke_restore\s+invoke_dependency_security\s+invoke_lint') 'The Bash validation action must scan restored dependencies before linting.'
  Assert-True ($cliSource -match "(?s)function Invoke-InitialAzurePathway.*?'1/3: Test'.*?Invoke-Tests.*?'2/3: Build'.*?Invoke-Build.*?'3/3: Provision and deploy'.*?Invoke-AzureUp") 'The PowerShell initial Azure pathway must show and run all three steps in order.'
  Assert-True ($cliSource -match "(?s)function Invoke-UpdateAzurePathway.*?'1/3: Test'.*?Invoke-Tests.*?'2/3: Build'.*?Invoke-Build.*?'3/3: Build, push, and deploy image'.*?Invoke-AzureDeploy") 'The PowerShell Azure update pathway must show and run all three steps in order.'
  Assert-True ($bashCliSource -match "(?s)invoke_initial_azure_pathway\(\).*?'1/3: Test'.*?invoke_tests.*?'2/3: Build'.*?invoke_build.*?'3/3: Provision and deploy'.*?invoke_azure_up") 'The Bash initial Azure pathway must show and run all three steps in order.'
  Assert-True ($bashCliSource -match "(?s)invoke_update_azure_pathway\(\).*?'1/3: Test'.*?invoke_tests.*?'2/3: Build'.*?invoke_build.*?'3/3: Build, push, and deploy image'.*?invoke_azure_deploy") 'The Bash Azure update pathway must show and run all three steps in order.'
  Assert-True ($cliSource -match '(?s)function Invoke-DependencyToolingUpdates\s*\{\s*Invoke-DependencyUpdates\s+Invoke-PnpmUpdate\s*\}') 'The PowerShell combined update action must update dependencies before the pnpm pin.'
  Assert-True ($bashCliSource -match '(?s)invoke_dependency_tooling_updates\(\)\s*\{\s*invoke_dependency_updates\s+invoke_pnpm_update\s*\}') 'The Bash combined update action must update dependencies before the pnpm pin.'
  Assert-True ($cliSource -match "(?s)\`$menuContext = 'Home'\s+Clear-Host\s+while \(\`$true\)") 'The PowerShell interactive menu must clear once before its first render.'
  Assert-True (([regex]::Matches($cliSource, '\bClear-Host\b')).Count -eq 1) 'The PowerShell interactive menu must not clear after actions or navigation.'
  Assert-True ($bashCliSource -match "(?s)MENU_CONTEXT='Home'\s+clear\s+while true") 'The Bash interactive menu must clear once before its first render.'
  Assert-True (([regex]::Matches($bashCliSource, '(?m)^\s*clear\s*$')).Count -eq 1) 'The Bash interactive menu must not clear after actions or navigation.'
  Assert-True ($cliSource -match '(?s)function Show-Menu.*?while \(\$true\) \{\s+Reset-ConsoleColors\s+Write-Host') 'The PowerShell menu must reset console colors before every render.'
  Assert-True ($bashCliSource -match '(?s)show_menu\(\).*?while true; do\s+reset_console_colors\s+printf') 'The Bash menu must reset console colors before every render.'
  Assert-True ($cliSource -match '(?s)do \{.*?Invoke-Action.*?Wait-ForMenuReturn.*?catch \{.*?Action failed:.*?Wait-ForMenuReturn -OfferRetry.*?\} while \(\$retryAction\)') 'The PowerShell menu must offer to retry failed actions and pause after successful actions.'
  Assert-True ($cliSource -match 'Press "r" for retry or any other key to return to the menu\.') 'The PowerShell failure prompt must describe retry and return behavior.'
  Assert-True ($bashCliSource -match 'Press "r" for retry or any other key to return to the menu\.') 'The Bash failure prompt must describe retry and return behavior.'
  Assert-True ($bashCliSource -match "(?s)run_menu_action\(\).*?trap '.*?Action failed:.*?wait_for_menu_return true.*?run_menu_action.*?else.*?wait_for_menu_return.*?' EXIT.*?invoke_action") 'The Bash menu must rerun failed actions when retry is selected and pause after successful actions.'
  Assert-True ($cliSource -match "Read-Host 'Select an action'\)\.ToLowerInvariant\(\)\s+Write-Host ''") 'The PowerShell menu must print a blank line after a selection.'
  Assert-True ($bashCliSource -match "read -r -p 'Select an action: ' selection \|\| true\s+printf '\\n'") 'The Bash menu must print a blank line after a selection.'
  $expectedActions = @(
    'menu', 'help', 'check', 'setup', 'dependencies-update', 'pnpm-update', 'restore', 'format', 'lint', 'build', 'run', 'run-tutorial', 'site-preview',
    'test', 'coverage', 'publish', 'validate', 'container-engine-wslc',
    'container-engine-docker', 'container-build', 'container-run', 'container-test',
    'container-showcase', 'container-logs', 'container-status', 'container-stop',
    'container-remove', 'azure-setup', 'azure-check', 'azure-up', 'azure-import',
    'azure-push', 'azure-deploy', 'azure-down', 'pathway-azure-initial',
    'pathway-azure-update', 'all'
  )
  foreach ($action in $expectedActions) {
    Assert-True ($bashCliSource -match "(?m)^VALID_ACTIONS=.*\b$([regex]::Escape($action))\b") "Bash CLI action '$action' is missing."
    if ($action -notin @('menu')) {
      Assert-True ($cliSource -match "'$([regex]::Escape($action))'") "PowerShell CLI action '$action' is missing."
    }
  }
  foreach ($option in @(
      '--install-missing', '--skip-container-check', '--container-engine',
      '--configuration', '--image-name', '--container-name', '--volume-name',
      '--nuget-source', '--port', '--environment-file', '--what-if', '--confirm'
    )) {
    Assert-True ($bashCliSource.Contains($option)) "Bash CLI option '$option' is missing."
  }
  foreach ($menuLabel in @(
      'Test, build, and deploy initial Azure environment',
      'Test, build, and update existing Azure deployment',
      'Setup local dependencies', 'Update dependencies and pnpm meeting the cooldown',
      'Run locally', 'Run without tutorial', 'Run with tutorial', 'Preview documentation site', 'Validate managed code',
      'Run unit tests', 'Use WSLC', 'Use Docker',
      'Build Dockerfile', 'Start or restart native container', 'Test running container',
      'Load and showcase built-in example', 'Show container logs', 'Show container status',
      'Stop container', 'Remove container', 'Set up Azure Tooling',
      'Validate deployment configuration', 'Provision and deploy',
      'Build, push, and deploy image', 'Build and push image only',
      'Import Docker Hub image and deploy', 'Delete Azure resources', 'Help', 'Quit'
    )) {
    Assert-True ($cliSource.Contains($menuLabel)) "PowerShell menu label '$menuLabel' is missing."
    Assert-True ($bashCliSource.Contains($menuLabel)) "Bash menu label '$menuLabel' is missing."
  }
  foreach ($requiredBashContract in @(
      '/__mockapi/api/configuration', '/__mockapi/api/endpoints',
      '/__mockapi/api/statistics', '/ctp/attractions/cloud-cruiser/wait-times',
      'build --pull --tag', 'image tag', 'MockApi__DashboardUsername=',
      'MockApi__DashboardPasswordHash=', 'publish mockapi --environment',
      'az bicep build', 'should_process'
    )) {
    Assert-True ($bashCliSource.Contains($requiredBashContract)) "Bash CLI contract '$requiredBashContract' is missing."
  }

  $bashHelp = Invoke-BashCliProcess -Arguments @('--action', 'help', '--what-if')
  Assert-True ($bashHelp.ExitCode -eq 0) "Bash help failed: $($bashHelp.Output)"
  foreach ($helpSection in @(
      '1) Pathways', '2) Setup', '3) Local Development', '4) Verification',
      '5) Container Operations (WSLC or Docker)', '6) Azure (Container Apps via azd)',
      '7) Misc'
    )) {
    Assert-True ($bashHelp.Output.Contains($helpSection)) "Bash help section '$helpSection' is missing."
  }
  $invalidBashPort = Invoke-BashCliProcess -Arguments @('--action', 'help', '--port', '0')
  Assert-True (
    $invalidBashPort.ExitCode -ne 0 -and $invalidBashPort.Output -match 'between 1 and 65535'
  ) 'Bash CLI must reject ports outside the PowerShell validation range.'

  Set-Content -LiteralPath $bashEnvironmentPath -Value @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_ENV_NAME=a',
    'UNSUPPORTED=value'
  )
  $bashEnvironmentFile = Split-Path -Leaf $bashEnvironmentPath
  $invalidBashEnvironment = Invoke-BashCliProcess -Arguments @(
    '--action', 'azure-check', '--environment-file', $bashEnvironmentFile
  )
  Assert-True (
    $invalidBashEnvironment.ExitCode -ne 0 -and
    $invalidBashEnvironment.Output -match "Unknown Azure deployment key 'UNSUPPORTED'"
  ) 'Bash CLI must reject unknown Azure deployment keys before invoking Azure tooling.'
  Assert-True ($cliSource -notmatch "'--volume',") 'WSLC volume arguments must use --volume=value because version 2.9.3.0 silently ignores separated values.'
  Assert-True ($cliSource -match '"--volume=\$\{VolumeName\}:/data"') 'Container workflows must attach the configured named volume at /data.'
  Assert-True ($cliSource -match 'Join-Path \$repositoryRoot ''Dockerfile''') 'Container builds must use Dockerfile.'
  Assert-True ($cliSource -match '--build-arg'', "NUGET_SOURCE=\$containerBuildNuGetSource"') 'PowerShell container builds must pass the resolved NuGet source to the Dockerfile.'
  Assert-True ($bashCliSource -match '--build-arg "NUGET_SOURCE=\$containerBuildNuGetSource"') 'Bash container builds must pass the resolved NuGet source to the Dockerfile.'
  Assert-True ($dockerfileSource -match 'ARG NUGET_SOURCE=https://api\.nuget\.org/v3/index\.json') 'Dockerfile must retain a portable public NuGet fallback.'
  Assert-True ($dockerfileSource -match 'dotnet restore .* --source "\$NUGET_SOURCE"') 'Dockerfile restore must use the selected NuGet source.'
  Assert-True ($dockerfileSource -match 'FROM mcr\.microsoft\.com/dotnet/sdk:10\.[^\s]+-aot@sha256:[0-9a-f]{64} AS build') 'Container compilation must use the pinned native AOT SDK.'
  foreach ($command in @('restore', 'publish')) {
    Assert-True ($dockerfileSource -match "dotnet $command [^\r\n]*-p:PublishAot=true") "Container $command must enable Native AOT."
  }
  foreach ($source in @($cliSource, $bashCliSource)) {
    Assert-True ($source.Contains('-p:PublishAot=false')) 'Managed cross-publication must remain explicitly separate from native AOT container publication.'
    Assert-True ($source.Contains('Cross-publish trimmed CoreCLR artifacts for managed-code checks, not release images.')) 'Both CLI help surfaces must distinguish diagnostic CoreCLR artifacts from release images.'
    Assert-True ($source.Contains('Build a native AOT image')) 'Both CLI help surfaces must identify native AOT container builds.'
  }
  & (Join-Path $PSScriptRoot 'NativePublication.Tests.ps1')
  Assert-True ($cliSource -match "'c1' = @\{ Action = 'container-engine-wslc' \}") 'Menu action c1 must select WSLC.'
  Assert-True ($cliSource -match "'c2' = @\{ Action = 'container-engine-docker' \}") 'Menu action c2 must select Docker.'
  Assert-True ($cliSource -match "\[ValidateSet\('wslc', 'docker'\)\]") 'The CLI must accept WSLC and Docker container engines.'
  Assert-True (([regex]::Matches($cliSource, "-Executable 'wslc'")).Count -eq 1) 'Only the WSLC version check may invoke wslc directly; container operations must use the selected engine.'
  Assert-True ($cliSource -match 'Removing dashboard assets makes only a very small difference') 'Container guidance must state that removing dashboard assets has minimal size impact.'
  Assert-True ($cliSource -match 'MockApi__EnableDashboard=false to disable the dashboard') 'Container guidance must identify the runtime dashboard switch.'
  Assert-True ($dockerfileSource -notmatch 'MockApi__EnableDashboard=false') 'Dockerfile must not disable the dashboard by default.'
  Assert-True ($cliSource -match 'for \(\$attempt = 1; \$attempt -le 5; \$attempt\+\+\)') 'PowerShell showcase must make five requests before asserting HTTP 429.'
  Assert-True (([regex]::Matches($cliSource, '\.TryGetValues\(')).Count -ge 2) 'PowerShell showcase must read optional response headers without throwing.'
  Assert-True ($bashCliSource -match 'for attempt in 1 2 3 4 5') 'Bash showcase must make five requests before asserting HTTP 429.'
  Assert-True ($cliSource -match '\$retryAfter -eq ''10''') 'PowerShell showcase must assert the checked-in Retry-After value.'
  Assert-True ($bashCliSource -match '\$retryAfter" == "10') 'Bash showcase must assert the checked-in Retry-After value.'
  $example = Get-Content -LiteralPath (Join-Path $repositoryRoot 'config\mockapi.json') -Raw | ConvertFrom-Json
  $waitTimes = $example.endpoints | Where-Object { $_.path -eq '/ctp/attractions/cloud-cruiser/wait-times' }
  foreach ($source in @($cliSource, $bashCliSource)) {
    Assert-True ($source.Contains($waitTimes.path)) 'Both showcases must use the Contoso Theme Parks wait-times route.'
    Assert-True ($source.Contains($waitTimes.response.body)) 'Both showcases must assert the exact configured wait-times error body.'
  }
  Assert-True ($cliSource.Contains('[string] $_.path -ceq $examplePath')) 'PowerShell showcase must detect older routes even when the stable ID exists.'
  Assert-True ($bashCliSource.Contains('entry.get("path") == os.environ["MOCKAPI_EXAMPLE_PATH"]')) 'Bash showcase must detect older routes even when the stable ID exists.'
  foreach ($source in @($cliSource, $bashCliSource)) {
    Assert-True ($source.Contains('MOCKAPI_KEY') -and $source.Contains('X-MockAPI-Key')) 'Both showcases must support the instance API key.'
    Assert-True ($source.Contains('MOCKAPI_DASHBOARD_USERNAME') -and $source.Contains('MOCKAPI_DASHBOARD_PASSWORD')) 'Both showcases must support paired administrator credentials.'
    Assert-True ($source.Contains('API key required. Set MOCKAPI_KEY to the key generated in dashboard Settings.')) 'Both showcases must give the same actionable API-key rejection message.'
  }
  Assert-True ($bashCliSource -match 'print\(total, matched, unmatched, endpoint\)\s*'' \| tr -d ''\\r''') 'Bash showcase statistics must normalize native Windows Python line endings before arithmetic.'
  Assert-True (
    ([regex]::Matches($cliSource, 'Write-AzureServiceSummary -ServiceUri \$serviceUri')).Count -eq 4
  ) 'Azure provisioning, deployment, publication, and import must finish with the Container App service summary.'
  Assert-True (
    $cliSource -match "(?s)function Invoke-AzureDown.*?Invoke-InteractiveTool -Executable 'az'.*?'group', 'delete'.*?'--subscription'.*?'--no-wait'"
  ) 'PowerShell Azure deletion must preserve the Azure CLI confirmation and return after submitting resource-group deletion.'
  Assert-True (
    $bashCliSource -match "(?s)invoke_azure_down\(\).*?run_interactive_tool 'Requesting asynchronous deletion of MockAPI Azure resources' az group delete.*?--subscription.*?--no-wait"
  ) 'Bash Azure deletion must use the mirrored interactive no-wait command path.'

  . (Import-CliFunction -Name 'ConvertFrom-SecureStringToPlainText')
  . (Import-CliFunction -Name 'New-DashboardPasswordHash')
  . (Import-CliFunction -Name 'Read-DashboardCredential')
  . (Import-CliFunction -Name 'Test-AzureDeployment')
  . (Import-CliFunction -Name 'Write-AzureServiceSummary')

  $script:readinessAttempts = 0
  $script:readinessProgress = [Collections.Generic.List[string]]::new()
  function Write-Host {
    param([string] $Object, [ConsoleColor] $ForegroundColor)
    $script:readinessProgress.Add($Object)
  }
  function Get-AzureServiceUri {
    param([string] $EnvironmentName)
    Assert-True (($script:readinessProgress -join "`n").Contains('Next: verify Azure deployment readiness.')) 'The next step must be printed before querying azd for the endpoint.'
    return 'https://mockapi.example.test'
  }
  function Invoke-WebRequest {
    $script:readinessAttempts++
    Assert-True ($script:readinessProgress[-1] -eq "Readiness check ($script:readinessAttempts/6): https://mockapi.example.test/health/ready") 'Each HTTP request must be preceded by its attempt number and target.'
    if ($script:readinessAttempts -eq 1) {
      throw [TimeoutException]::new('cold start')
    }
    return [pscustomobject]@{ StatusCode = 200; Content = '{"status":"ready"}' }
  }
  function Start-Sleep { param([int] $Seconds) }
  function Write-Field { param([string] $Label, [string] $Value, [ConsoleColor] $Color) }

  $serviceUri = Test-AzureDeployment -EnvironmentName 'mockapi-dev'
  Assert-True ($script:readinessAttempts -eq 2) 'Azure smoke testing must retry a transient readiness timeout.'
  Assert-True ($serviceUri -eq 'https://mockapi.example.test') 'Azure smoke testing must return the validated Container App URL.'
  Assert-True ($script:readinessProgress.Contains('Not ready yet; retrying in 5 seconds.')) 'Cold-start retries must explain the wait.'
  Remove-Item Function:\Get-AzureServiceUri
  Remove-Item Function:\Invoke-WebRequest
  Remove-Item Function:\Start-Sleep
  Remove-Item Function:\Write-Field
  Remove-Item Function:\Write-Host

  $bashReadiness = @'
#!/usr/bin/env bash
set -Eeuo pipefail
ready=false
write_color() { printf '%s\n' "$2"; }
write_field() { :; }
get_azure_service_uri() { printf 'https://mockapi.example.test'; }
sleep() { ready=true; }
die() { printf '%s\n' "$1" >&2; exit 1; }
curl() {
  if [[ "$ready" == false ]]; then printf '503'
  else printf '{"status":"ready"}' > "$3"; printf '200'; fi
}
'@
  $bashReadiness += "`n" + [regex]::Match($bashCliSource, '(?ms)^test_azure_deployment\(\) \{.*?^\}').Value + "`n" + 'test_azure_deployment fixture'
  Set-Content -LiteralPath $bashMenuTestPath -Value $bashReadiness.Replace("`r", '') -Encoding utf8NoBOM -NoNewline
  $bashReadinessResult = Invoke-BashCliProcess -ScriptPath ('./' + (Split-Path -Leaf $bashMenuTestPath)) -Arguments @('fixture')
  Assert-True ($bashReadinessResult.ExitCode -eq 0) "Bash readiness progress failed: $($bashReadinessResult.Output)"
  Assert-True ($bashReadinessResult.Output.Replace("`r", '').Trim() -ceq ($script:readinessProgress -join "`n").Trim()) 'Both shells must print identical readiness progress and retry messages.'

  $script:azureSummaryLines = [Collections.Generic.List[string]]::new()
  function Write-Host {
    param(
      [Parameter(Position = 0, ValueFromRemainingArguments)][object[]] $Object,
      [ConsoleColor] $ForegroundColor
    )
    $script:azureSummaryLines.Add(($Object -join ' '))
  }
  $bashSummary = @'
#!/usr/bin/env bash
set -Eeuo pipefail
write_color() { printf '%s\n' "$2"; }
'@
  $bashSummary += "`n" + [regex]::Match($bashCliSource, '(?ms)^write_azure_service_summary\(\) \{.*?^\}').Value + "`n" + 'write_azure_service_summary "$1"'
  Set-Content -LiteralPath $bashMenuTestPath -Value $bashSummary.Replace("`r", '') -Encoding utf8NoBOM -NoNewline
  foreach ($uri in @('https://mockapi.example.test', 'https://mockapi.example.test/')) {
    $script:azureSummaryLines.Clear()
    Write-AzureServiceSummary -ServiceUri $uri
    $expectedSummary = "`nAll done! The Azure action completed successfully.`n`nDashboard URL:`nhttps://mockapi.example.test/"
    Assert-True (
      ($script:azureSummaryLines -join "`n") -ceq $expectedSummary
    ) 'Azure actions must end with a completion summary and labeled dashboard URL with one trailing slash.'
    $bashSummaryResult = Invoke-BashCliProcess -ScriptPath ('./' + (Split-Path -Leaf $bashMenuTestPath)) -Arguments @($uri)
    Assert-True ($bashSummaryResult.ExitCode -eq 0) "Bash Azure summary failed: $($bashSummaryResult.Output)"
    Assert-True ($bashSummaryResult.Output.Replace("`r", '').TrimEnd("`n") -ceq $expectedSummary) 'Both shells must print identical Azure completion summaries.'
  }
  Remove-Item Function:\Write-Host

  $passwordText = 'correct horse battery staple'
  $securePassword = ConvertTo-SecureString $passwordText -AsPlainText -Force
  $encodedHash = New-DashboardPasswordHash -Password $securePassword
  $hashParts = $encodedHash.Split('.')
  Assert-True ($hashParts.Length -eq 4 -and $hashParts[0] -eq 'v1') 'Dashboard password hash must use the versioned four-part format.'
  Assert-True ($hashParts[1] -eq '600000') 'Dashboard password hash must use 600,000 PBKDF2 iterations.'
  $expectedHash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
    $passwordText,
    [Convert]::FromBase64String($hashParts[2]),
    600000,
    [Security.Cryptography.HashAlgorithmName]::SHA256,
    32
  )
  Assert-True (
    [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
      $expectedHash,
      [Convert]::FromBase64String($hashParts[3])
    )
  ) 'Dashboard password hash must be verifiable with PBKDF2-SHA256.'
  Assert-True (-not $encodedHash.Contains($passwordText)) 'Dashboard password hash must not contain the plaintext password.'

  $script:readHostResponses = [Collections.Generic.Queue[object]]::new()
  function Read-Host {
    param([string] $Prompt, [switch] $AsSecureString)
    return $script:readHostResponses.Dequeue()
  }

  $script:menuLines = [Collections.Generic.List[string]]::new()
  function Write-Host {
    param(
      [Parameter(Position = 0, ValueFromRemainingArguments)][object[]] $Object,
      [ConsoleColor] $ForegroundColor
    )
    $script:menuLines.Add(($Object -join ' '))
  }

  Remove-Item Function:\Write-Host

  $script:readHostResponses.Enqueue('')
  Assert-True ($null -eq (Read-DashboardCredential)) 'Blank dashboard username must disable authentication.'

  $script:readHostResponses.Enqueue('operator')
  $script:readHostResponses.Enqueue((ConvertTo-SecureString 'short7!' -AsPlainText -Force))
  $script:readHostResponses.Enqueue((ConvertTo-SecureString 'short7!' -AsPlainText -Force))
  try {
    $null = Read-DashboardCredential
    throw 'Short dashboard password was accepted.'
  }
  catch {
    Assert-True ($_.Exception.Message -match 'at least 8') "Short dashboard passwords must be rejected. Received: $($_.Exception.Message)"
  }

  $script:readHostResponses.Enqueue('operator')
  $script:readHostResponses.Enqueue((ConvertTo-SecureString 'eight888' -AsPlainText -Force))
  $script:readHostResponses.Enqueue((ConvertTo-SecureString 'eight888' -AsPlainText -Force))
  $minimumCredential = Read-DashboardCredential
  Assert-True ($minimumCredential.Username -eq 'operator') 'An eight-character dashboard password must be accepted.'

  $script:readHostResponses.Enqueue('operator')
  $script:readHostResponses.Enqueue((ConvertTo-SecureString 'long-enough-password' -AsPlainText -Force))
  $script:readHostResponses.Enqueue((ConvertTo-SecureString 'different-password' -AsPlainText -Force))
  try {
    $null = Read-DashboardCredential
    throw 'Mismatched dashboard passwords were accepted.'
  }
  catch {
    Assert-True ($_.Exception.Message -match 'do not match') 'Mismatched dashboard passwords must be rejected.'
  }

  $null = New-Item -ItemType Directory -Path $stubDirectory -Force
  Set-Content -LiteralPath (Join-Path $stubDirectory 'azd.cmd') -Value @'
@echo off
echo %*>>"%MOCKAPI_TEST_COMMAND_LOG%"
if "%1"=="version" echo azd version 1.27.1 (stable)& exit /b 0
if "%1 %2 %3"=="auth login --check-status" if not exist "%MOCKAPI_TEST_LOGIN_MARKER%" exit /b 1
if "%1 %2 %3"=="auth login --check-status" exit /b 0
if "%1 %2"=="auth login" type nul >"%MOCKAPI_TEST_LOGIN_MARKER%"& exit /b 0
if "%1 %2"=="env select" if not exist "%MOCKAPI_TEST_ENVIRONMENT_MARKER%" exit /b 1
if "%1 %2"=="env new" type nul >"%MOCKAPI_TEST_ENVIRONMENT_MARKER%"& exit /b 0
if "%1 %2"=="env get-values" if "%MOCKAPI_TEST_VALUES_FAILURE%"=="true" exit /b 1
if "%1 %2"=="env get-values" if "%MOCKAPI_TEST_VALUES_INVALID%"=="true" echo invalid-json& exit /b 0
if "%1 %2"=="env get-values" if "%MOCKAPI_TEST_VALUES_ARRAY%"=="true" echo [{}]& exit /b 0
if "%1 %2"=="env get-values" echo {}& exit /b 0
if "%1 %2 %3"=="env get-value SERVICE_MOCKAPI_URI" if defined MOCKAPI_TEST_SERVICE_URI (echo %MOCKAPI_TEST_SERVICE_URI%) else (echo https://mockapi.example.test)& exit /b 0
if "%1 %2 %3"=="env get-value AZURE_CONTAINER_REGISTRY_ENDPOINT" echo mockapiregistry.azurecr.io& exit /b 0
exit /b 0
'@
  Set-Content -LiteralPath (Join-Path $stubDirectory 'az.cmd') -Value @'
@echo off
echo az %*>>"%MOCKAPI_TEST_COMMAND_LOG%"
if "%1"=="version" echo {"azure-cli":"2.77.0"}& exit /b 0
if "%1 %2"=="resource list" echo mockapi-container-app& exit /b 0
exit /b 0
'@

    Set-Content -LiteralPath $commandLog -Value '' -NoNewline
    $result = Invoke-CliProcess -Arguments @('-Action', 'azure-setup')
    Assert-True ($result.ExitCode -eq 0) "Azure tooling setup failed: $($result.Output)"
    Assert-True ($result.Output -match 'Azure Dev CLI\s+: 1\.27\.1' -and $result.Output -match 'Azure CLI\s+: 2\.77\.0') 'Azure tooling setup must report both CLI versions.'
    $commands = Get-Content -LiteralPath $commandLog -Raw
    Assert-True ($commands -match '(?m)^version\r?$' -and $commands -match 'az version --output json') 'Azure tooling setup must verify azd and Azure CLI versions.'
    Assert-True ($commands -notmatch 'auth login') 'Azure tooling setup must not authenticate.'

  $unknownFile = New-EnvironmentFile -Name 'unknown.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_ENV_NAME=mockapi-dev',
    'UNSUPPORTED=value'
  )
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $unknownFile)
  Assert-True ($result.ExitCode -ne 0 -and $result.Output -match "Unknown Azure deployment key 'UNSUPPORTED'") 'Unknown .env keys must be rejected.'

  $duplicateFile = New-EnvironmentFile -Name 'duplicate.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_LOCATION=westus2',
    'AZURE_ENV_NAME=mockapi-dev'
  )
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $duplicateFile)
  Assert-True ($result.ExitCode -ne 0 -and $result.Output -match "Duplicate Azure deployment key 'AZURE_LOCATION'") 'Duplicate .env keys must be rejected.'

  $partialDashboardFile = New-EnvironmentFile -Name 'partial-dashboard.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_ENV_NAME=mockapi-dev',
    'AZURE_DASHBOARD_USERNAME=operator',
    'AZURE_DASHBOARD_PASSWORD='
  )
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $partialDashboardFile)
  Assert-True (
    $result.ExitCode -ne 0 -and
    $result.Output -match '(?s)AZURE_DASHBOARD_USERNAME.*AZURE_DASHBOARD_PASSWORD.*must both be.*set.*or both be.*empty'
  ) "Partial dashboard credentials must be rejected. Received: $($result.Output)"

  $shortDashboardPasswordFile = New-EnvironmentFile -Name 'short-dashboard-password.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_ENV_NAME=mockapi-dev',
    'AZURE_DASHBOARD_USERNAME=operator',
    'AZURE_DASHBOARD_PASSWORD=short7!'
  )
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $shortDashboardPasswordFile)
  Assert-True (
    $result.ExitCode -ne 0 -and
    $result.Output -match 'AZURE_DASHBOARD_PASSWORD' -and
    $result.Output -match 'least 8 characters'
  ) "Short dashboard passwords must be rejected. Received: $($result.Output)"

  $invalidDashboardUsernameFile = New-EnvironmentFile -Name 'invalid-dashboard-username.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_ENV_NAME=mockapi-dev',
    'AZURE_DASHBOARD_USERNAME=invalid:operator',
    'AZURE_DASHBOARD_PASSWORD=correct horse battery staple'
  )
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $invalidDashboardUsernameFile)
  Assert-True (
    $result.ExitCode -ne 0 -and
    $result.Output -match 'AZURE_DASHBOARD_USERNAME' -and
    $result.Output -match 'colon\.'
  ) "Dashboard usernames containing a colon must be rejected. Received: $($result.Output)"

  $validFile = New-EnvironmentFile -Name 'valid.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=',
    'AZURE_LOCATION=westus2',
    'AZURE_ENV_NAME=file-environment'
  )
  $processEnvironment = @{
    AZURE_SUBSCRIPTION_ID = '00000000-0000-0000-0000-000000000000'
    AZURE_LOCATION = 'eastus2'
    AZURE_ENV_NAME = 'process-environment'
  }
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $validFile) -Environment $processEnvironment
  Assert-True ($result.ExitCode -eq 0) "Valid Azure preflight failed: $($result.Output)"
  Assert-True ($result.Output -match 'process-environment' -and $result.Output -match 'eastus2') 'Process environment values must take precedence over .env values.'

  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True ($commands -match 'auth login --check-status --no-prompt' -and $commands -match '(?m)^auth login\r?$') 'Expired authentication must trigger interactive login and retry the status check.'
  Assert-True ($commands -match 'env new process-environment') 'A missing azd environment must be created with the configured name.'
  Assert-True ($commands -match 'show --environment process-environment --no-prompt') 'Azure preflight must parse the azd project configuration.'
  Assert-True ($commands -match 'az bicep build --file') 'Azure preflight must build the Bicep entry point.'
  Assert-True ($commands -match 'env set AZURE_DASHBOARD_USERNAME=') 'Empty dashboard credentials must clear the azd username value.'
  Assert-True ($commands -match 'env set AZURE_DASHBOARD_PASSWORD_HASH=') 'Empty dashboard credentials must clear the azd password hash value.'

  $dashboardPassword = 'eight888'
  $dashboardFile = New-EnvironmentFile -Name 'dashboard.env' -Lines @(
    'AZURE_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000',
    'AZURE_LOCATION=eastus2',
    'AZURE_ENV_NAME=dashboard-environment',
    'AZURE_DASHBOARD_USERNAME=operator',
    "AZURE_DASHBOARD_PASSWORD=$dashboardPassword"
  )
  Clear-Content -LiteralPath $commandLog
  $result = Invoke-CliProcess -Arguments @('-Action', 'azure-check', '-EnvironmentFile', $dashboardFile)
  Assert-True ($result.ExitCode -eq 0) "Dashboard-authenticated Azure preflight failed: $($result.Output)"
  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True ($commands -match 'env set AZURE_DASHBOARD_USERNAME operator') 'Configured dashboard username must be synchronized to azd.'
  Assert-True ($commands -match 'env set AZURE_DASHBOARD_PASSWORD_HASH v1\.600000\.') 'A versioned dashboard password hash must be synchronized to azd.'
  Assert-True ($commands -notmatch [regex]::Escape($dashboardPassword)) 'The plaintext dashboard password must never be passed to azd.'

  Clear-Content -LiteralPath $commandLog
  $result = Invoke-CliProcess -Arguments @(
    '-Action', 'azure-push', '-EnvironmentFile', $validFile, '-Confirm:$false'
  ) -Environment $processEnvironment
  Assert-True ($result.ExitCode -eq 0) "Azure push failed: $($result.Output)"
  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True ($commands -match 'publish mockapi --environment process-environment --no-prompt') 'Azure push must publish the MockAPI service to the selected environment registry.'
  Assert-True ($commands -match 'publish mockapi[\s\S]*env get-value SERVICE_MOCKAPI_URI') 'Azure push must resolve the deployed Container App URL after publication succeeds.'
  Assert-True ($commands -notmatch '(^|\s)deploy(\s|$)') 'Azure push must not deploy the published image.'
  Assert-True (
    ($result.Output.TrimEnd() -split "`r?`n")[-1] -eq 'https://mockapi.example.test/'
  ) 'Azure push must print the deployed Container App URL as its final output line.'

  $portReservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $portReservation.Start()
  $readinessPort = ([Net.IPEndPoint] $portReservation.LocalEndpoint).Port
  $portReservation.Stop()
  $readinessJob = Start-Job -ArgumentList $readinessPort -ScriptBlock {
    param([int] $Port)
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$Port/")
    try {
      $listener.Start()
      $context = $listener.GetContext()
      $responseBytes = [Text.Encoding]::UTF8.GetBytes('{"status":"ready"}')
      $context.Response.StatusCode = 200
      $context.Response.ContentType = 'application/json'
      $context.Response.ContentLength64 = $responseBytes.Length
      $context.Response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
      $context.Response.Close()
    }
    finally {
      $listener.Close()
    }
  }
  $importEnvironment = $processEnvironment.Clone()
  $importEnvironment.MOCKAPI_TEST_SERVICE_URI = "http://127.0.0.1:$readinessPort"
  Clear-Content -LiteralPath $commandLog
  try {
    $result = Invoke-CliProcess -Arguments @(
      '-Action', 'azure-import', '-EnvironmentFile', $validFile, '-Confirm:$false'
    ) -Environment $importEnvironment
  }
  finally {
    $null = Wait-Job -Job $readinessJob -Timeout 5
    Remove-Job -Job $readinessJob -Force
  }
  Assert-True ($result.ExitCode -eq 0) "Azure import failed: $($result.Output)"
  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True (
    $commands -match "az acr import --name mockapiregistry --source docker\.io/simonkurtzmsft/mockapi:v$escapedApplicationVersion --image mockapi:v$escapedApplicationVersion"
  ) "Azure import must copy the public versioned Docker Hub image into ACR without credentials. Commands:`n$commands"
  Assert-True (
    $commands -match "az containerapp update --name mockapi-container-app --resource-group rg-process-environment-eastus2 --image mockapiregistry\.azurecr\.io/mockapi:v$escapedApplicationVersion"
  ) 'Azure import must deploy the imported ACR image to the tagged Container App.'
  Assert-True (
    $commands -match 'az resource list[^\r\n]*--resource-group rg-process-environment-eastus2[^\r\n]*--tag azd-service-name=mockapi[^\r\n]*Microsoft\.App/containerApps[^\r\n]*--output tsv'
  ) 'Azure import must discover the tagged Container App without combining incompatible Azure CLI filters.'
  Assert-True (
    $commands -notmatch 'az resource list[^\r\n]*--resource-type[^\r\n]*--tag'
  ) 'Azure resource discovery must not combine mutually exclusive --resource-type and --tag filters.'
  Assert-True ($commands -notmatch '--username|--password') 'Public Docker Hub imports must not pass registry credentials.'
  Assert-True (
    ($result.Output.TrimEnd() -split "`r?`n")[-1] -eq "http://127.0.0.1:$readinessPort/"
  ) 'Azure import must print the deployed Container App URL as its final output line.'

  Clear-Content -LiteralPath $commandLog
  $result = Invoke-CliProcess -Arguments @(
    '-Action', 'azure-import', '-EnvironmentFile', $validFile, '-WhatIf'
  ) -Environment $processEnvironment
  Assert-True ($result.ExitCode -eq 0) "Azure import WhatIf failed: $($result.Output)"
  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True ($commands -notmatch 'az acr import|az containerapp update') '-WhatIf must prevent image import and Container App updates.'

  Clear-Content -LiteralPath $commandLog
  $result = Invoke-CliProcess -Arguments @(
    '-Action', 'azure-push', '-EnvironmentFile', $validFile, '-WhatIf'
  ) -Environment $processEnvironment
  Assert-True ($result.ExitCode -eq 0) "Azure push WhatIf failed: $($result.Output)"
  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True ($commands -notmatch '(^|\s)publish(\s|$)') '-WhatIf must prevent azd publish execution.'

  Clear-Content -LiteralPath $commandLog
  $result = Invoke-CliProcess -Arguments @(
    '-Action', 'azure-deploy', '-EnvironmentFile', $validFile, '-WhatIf'
  ) -Environment $processEnvironment
  Assert-True ($result.ExitCode -eq 0) "Azure deploy WhatIf failed: $($result.Output)"
  $commands = Get-Content -LiteralPath $commandLog -Raw
  Assert-True ($commands -notmatch '(^|\s)deploy(\s|$)') '-WhatIf must prevent azd deploy execution.'

  Write-Host 'Developer CLI tests passed.' -ForegroundColor Green
}
finally {
  Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $bashEnvironmentPath -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $bashMenuTestPath -Force -ErrorAction SilentlyContinue
}
