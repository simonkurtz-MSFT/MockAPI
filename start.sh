#!/usr/bin/env bash
# MockAPI Developer CLI (bash).
#
# This script is the bash counterpart to start.ps1 and MUST stay completely
# aligned with it: identical actions, interactive menu entries and ordering,
# options, help text, and behavior. When you change one script, change the
# other (and their tests) in the same commit.
#
# PowerShell option names map to bash long flags:
#   -Action            -> --action
#   -InstallMissing    -> --install-missing
#   -SkipContainerCheck-> --skip-container-check
#   -ContainerEngine   -> --container-engine
#   -Configuration     -> --configuration
#   -ImageName         -> --image-name
#   -ContainerName     -> --container-name
#   -VolumeName        -> --volume-name
#   -NuGetSource       -> --nuget-source
#   -Port              -> --port
#   -EnvironmentFile   -> --environment-file

set -Eeuo pipefail

# ---------------------------------------------------------------------------
# Global configuration and defaults (mirrors start.ps1 parameters).
# ---------------------------------------------------------------------------
ACTION="menu"
INSTALL_MISSING="false"
SKIP_CONTAINER_CHECK="false"
CONTAINER_ENGINE="wslc"
CONFIGURATION="Release"
IMAGE_NAME="mockapi:dev"
CONTAINER_NAME="mockapi-dev"
VOLUME_NAME="mockapi-data"
NUGET_SOURCE=""
PORT="8080"
ENVIRONMENT_FILE=".env"
WHAT_IF="false"
CONFIRM="false"

# Resolve the repository root as the directory that contains this script.
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd -P)"
REPOSITORY_ROOT="$SCRIPT_DIR"

# The minimum azd version that supports the workflows this script drives.
MINIMUM_AZD_VERSION="1.27.1"

# The container image identifier label used to detect stale containers.
IMAGE_ID_LABEL="mockapi.image-id"

# The uid:gid the container process runs as; the data volume must be writable by it.
CONTAINER_USER="1654:1654"

# The public Docker Hub repository that publishes released images.
PUBLIC_IMAGE_REPOSITORY="docker.io/simonkurtzmsft/mockapi"

# Dashboard credential state captured when (re)creating a container.
DASHBOARD_USERNAME=""
DASHBOARD_PASSWORD=""
DASHBOARD_PASSWORD_HASH=""
AZURE_CONFIGURATION=""
AZURE_CONFIGURATION_ENVIRONMENT_NAME=""
AZURE_CONFIGURATION_LOCATION=""
AZURE_CONFIGURATION_SUBSCRIPTION=""
AZURE_SERVICE_URI=""

# The interactive menu writes the chosen action here.
MENU_SELECTION=""
DOMAIN_CONTINUATION_PROMPTED=false

# ---------------------------------------------------------------------------
# Console helpers.
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
  COLOR_RESET=$'\033[0m'
  COLOR_RED=$'\033[31m'
  COLOR_GREEN=$'\033[32m'
  COLOR_YELLOW=$'\033[33m'
  COLOR_CYAN=$'\033[36m'
  COLOR_DARKCYAN=$'\033[36m'
else
  COLOR_RESET=""
  COLOR_RED=""
  COLOR_GREEN=""
  COLOR_YELLOW=""
  COLOR_CYAN=""
  COLOR_DARKCYAN=""
fi

reset_console_colors() {
  if [[ -t 1 ]]; then
    printf '%s' "$COLOR_RESET"
  fi
}

trap reset_console_colors EXIT

write_color() {
  # $1 = color name, $2 = text
  local color="$COLOR_RESET"
  case "$1" in
    red) color="$COLOR_RED" ;;
    green) color="$COLOR_GREEN" ;;
    yellow) color="$COLOR_YELLOW" ;;
    cyan) color="$COLOR_CYAN" ;;
    darkcyan) color="$COLOR_DARKCYAN" ;;
  esac
  printf '%s%s%s\n' "$color" "$2" "$COLOR_RESET"
}

write_field() {
  # $1 = name, $2 = value, $3 = optional color for the value
  local name="$1" value="$2" color="${3:-}"
  local colorCode="$COLOR_RESET"
  case "$color" in
    red) colorCode="$COLOR_RED" ;;
    green) colorCode="$COLOR_GREEN" ;;
    yellow) colorCode="$COLOR_YELLOW" ;;
    cyan) colorCode="$COLOR_CYAN" ;;
    *) colorCode="" ;;
  esac
  if [[ -n "$colorCode" ]]; then
    printf '%-20s: %s%s%s\n' "$name" "$colorCode" "$value" "$COLOR_RESET"
  else
    printf '%-20s: %s\n' "$name" "$value"
  fi
}

die() {
  write_color red "$1" >&2
  exit 1
}

trim() {
  local s="$1"
  s="${s#"${s%%[![:space:]]*}"}"
  s="${s%"${s##*[![:space:]]}"}"
  printf '%s' "$s"
}

run_tool() {
  # $1 = operation description, remaining args = command to execute.
  local operation="$1"
  shift
  printf '\n'
  write_color cyan "$operation"
  set +e
  "$@"
  local exitCode=$?
  set -e
  [[ $exitCode -eq 0 ]] || die "$operation failed with exit code $exitCode."
}

run_interactive_tool() {
  # $1 = operation description, remaining args = command to execute.
  local operation="$1"
  shift
  printf '\n'
  write_color cyan "$operation"
  set +e
  "$@"
  local exitCode=$?
  set -e
  [[ $exitCode -eq 0 ]] || die "$operation failed with exit code $exitCode."
}

require_command() {
  command -v "$1" >/dev/null 2>&1
}

should_process() {
  local target="$1" operation="$2"
  if [[ "$WHAT_IF" == 'true' ]]; then
    printf "What if: Performing the operation '%s' on target '%s'.\n" "$operation" "$target"
    return 1
  fi
  if [[ "$CONFIRM" == 'true' ]]; then
    local response
    read -r -p "$operation on '$target'? [y/N] " response || true
    [[ "$response" =~ ^[Yy]$ ]]
    return
  fi
  return 0
}

# ---------------------------------------------------------------------------
# Version and project metadata helpers.
# ---------------------------------------------------------------------------
get_application_version() {
  local projectFile="$REPOSITORY_ROOT/src/MockAPI/MockAPI.csproj"
  [[ -f "$projectFile" ]] || die "MockAPI.csproj was not found at $projectFile."
  local version
  version="$(grep -oE '<Version>[^<]+</Version>' "$projectFile" | head -n 1 | sed -E 's|</?Version>||g')"
  version="$(trim "$version")"
  [[ -n "$version" ]] || die "MockAPI.csproj does not define <Version>."
  printf '%s' "$version"
}

# ---------------------------------------------------------------------------
# Prerequisite discovery.
# ---------------------------------------------------------------------------
get_windows_command() {
  local name="$1"
  if require_command "$name"; then
    command -v "$name"
  elif require_command "${name}.exe"; then
    command -v "${name}.exe"
  else
    return 1
  fi
}

install_with_winget() {
  local packageId="$1" operation="$2" verb="${3:-install}" winget
  winget="$(get_windows_command winget)" || die "$operation requires winget, which is unavailable on this host."
  if should_process "$packageId" "$operation"; then
    run_tool "$operation" "$winget" "$verb" --id "$packageId" --exact \
      --accept-package-agreements --accept-source-agreements
    hash -r
  fi
}

install_dotnet_sdk() {
  install_with_winget 'Microsoft.DotNet.SDK.10' 'Installing .NET 10 SDK'
}

install_wslc() {
  local wsl
  wsl="$(get_windows_command wsl)" || die 'WSLC is missing and WSL is unavailable. Install or update WSL before using container actions.'
  if should_process 'Windows Subsystem for Linux' 'Update to install WSLC'; then
    run_tool 'Updating WSL and WSLC' "$wsl" --update
    hash -r
  fi
}

install_azd() {
  local verb='install' operation='Installing Azure Developer CLI'
  if require_command azd; then
    verb='upgrade'
    operation='Upgrading Azure Developer CLI'
  fi
  install_with_winget 'Microsoft.Azd' "$operation" "$verb"
}

install_azure_cli() {
  install_with_winget 'Microsoft.AzureCLI' 'Installing Azure CLI'
}

assert_dotnet() {
  if ! require_command dotnet; then
    [[ "$INSTALL_MISSING" == 'true' ]] || die "The .NET SDK is missing. Run './start.sh --action setup --install-missing'."
    install_dotnet_sdk
  fi
  require_command dotnet || die 'The .NET SDK is still unavailable after installation. Restart the terminal and run setup again.'
  local resolvedVersion
  resolvedVersion="$(dotnet --version 2>/dev/null)" || die 'Unable to resolve the .NET SDK version.'
  if [[ ! "$resolvedVersion" =~ ^10\. && "$INSTALL_MISSING" == 'true' ]]; then
    install_dotnet_sdk
    resolvedVersion="$(dotnet --version 2>/dev/null)" || die 'Unable to resolve the .NET SDK version after installation.'
  fi
  [[ "$resolvedVersion" =~ ^10\. ]] || die "Repository requires a .NET 10 SDK, but this directory resolves $resolvedVersion."
  write_field '.NET SDK' "$resolvedVersion" green
}

assert_pnpm() {
  require_command pnpm || die "pnpm is missing. Install Node.js from '.nvmrc', enable Corepack, and run 'corepack install'."
  local version
  version="$(pnpm --version 2>/dev/null)" || die 'Unable to resolve the pnpm version.'
  write_field 'pnpm' "$version" green
}

assert_wslc() {
  if ! require_command wslc; then
    [[ "$INSTALL_MISSING" == 'true' ]] || die "WSLC is missing. Update WSL or run './start.sh --action setup --install-missing'."
    install_wslc
  fi
  require_command wslc || die 'WSLC is still unavailable after the WSL update. Restart the terminal and run the check again.'
  local version
  version="$(wslc version 2>/dev/null | head -n 1)" || die 'Unable to resolve the WSLC version.'
  write_field 'WSLC' "$version" green
}

assert_docker() {
  require_command docker || die 'Docker is missing. Install Docker Desktop or another Docker CLI and engine, then run the check again.'
  local version
  version="$(docker version --format '{{.Client.Version}}' 2>/dev/null)" || die 'Docker is installed, but its engine is unavailable. Start the Docker engine and run the check again.'
  write_field 'Docker' "$version" green
}

assert_container_engine() {
  if [[ "$CONTAINER_ENGINE" == 'docker' ]]; then
    assert_docker
  else
    assert_wslc
  fi
}

test_prerequisites() {
  assert_dotnet
  assert_pnpm
  assert_container_engine
  write_field 'Solution' "$REPOSITORY_ROOT/MockAPI.slnx"
  write_field 'Container engine' "${CONTAINER_ENGINE^^}"
  printf '\n'
  write_color green 'Prerequisite checks passed.'
}

# ---------------------------------------------------------------------------
# Setup / restore / format / lint / build.
# ---------------------------------------------------------------------------
invoke_setup() {
  test_prerequisites
  invoke_tooling_restore
  invoke_restore
  printf '\n'
  write_color green 'Local development setup is ready.'
}

invoke_tooling_restore() {
  assert_pnpm
  run_tool 'Restoring formatting tools' pnpm install --frozen-lockfile
  run_tool 'Normalizing the pnpm lockfile' \
    node -e "require('./scripts/lib/pnpm-lock-registry.cjs').normalizeWorkingLockfile()"
  run_tool 'Installing repository Git hooks' git config core.hooksPath .githooks
}

invoke_dependency_updates() {
  assert_pnpm
  printf '\n'
  write_color cyan 'Checking for dependency updates'
  local outdatedStatus=0
  pnpm outdated || outdatedStatus=$?
  if (( outdatedStatus > 1 )); then
    die "Dependency update check failed with exit code $outdatedStatus."
  fi
  run_tool 'Installing the latest stable dependency updates that completed the eight-day cooldown' \
    node "$REPOSITORY_ROOT/scripts/lib/dependency-updater.cjs"
}

invoke_pnpm_update() {
  assert_pnpm
  require_command node || die 'Node.js is missing. Install the version selected by .nvmrc.'
  run_tool 'Updating pnpm to the latest release that completed the eight-day cooldown' \
    node "$REPOSITORY_ROOT/scripts/lib/pnpm-updater.cjs"
}

invoke_dependency_tooling_updates() {
  invoke_dependency_updates
  invoke_pnpm_update
}

invoke_restore() {
  assert_dotnet
  run_tool 'Restoring NuGet packages' dotnet restore "$REPOSITORY_ROOT/MockAPI.slnx"
}

invoke_format() {
  assert_dotnet
  assert_pnpm
  run_tool 'Formatting repository files' pnpm run format
}

invoke_lint() {
  assert_dotnet
  assert_pnpm
  run_tool 'Checking repository formatting and Markdown' pnpm run lint
}

invoke_build() {
  assert_dotnet
  run_tool "Building solution ($CONFIGURATION)" \
    dotnet build "$REPOSITORY_ROOT/MockAPI.slnx" --configuration "$CONFIGURATION" \
    -p:TreatWarningsAsErrors=true
}

get_local_application_url() {
  local configuredUrls="${ASPNETCORE_URLS:-http://localhost:5000}" configuredUrl
  local -a urls
  IFS=';' read -r -a urls <<< "$configuredUrls"
  for configuredUrl in "${urls[@]}"; do
    [[ "$configuredUrl" =~ ^https?:// ]] || continue
    [[ "$configuredUrl" =~ ^(https?)://([^/?#[:space:]]+) ]] || die "ASPNETCORE_URLS contains an invalid URL '$configuredUrl'."
    local scheme="${BASH_REMATCH[1]}" authority="${BASH_REMATCH[2]}"
    authority="$(printf '%s' "$authority" | sed -E 's/^(\*|\+|0\.0\.0\.0|\[::\])(:|$)/localhost\2/')"
    printf '%s://%s/\n' "$scheme" "$authority"
    return 0
  done
  die 'ASPNETCORE_URLS must contain at least one HTTP or HTTPS URL.'
}

open_local_browser() {
  local applicationUrl="$1"
  if command -v wslview >/dev/null 2>&1; then
    wslview "$applicationUrl"
  elif command -v xdg-open >/dev/null 2>&1; then
    xdg-open "$applicationUrl"
  elif command -v open >/dev/null 2>&1; then
    open "$applicationUrl"
  elif command -v powershell.exe >/dev/null 2>&1; then
    MOCKAPI_BROWSER_URL="$applicationUrl" powershell.exe -NoProfile -Command 'Start-Process -FilePath $env:MOCKAPI_BROWSER_URL'
  else
    printf "WARNING: No browser launcher is available. Open '%s' manually.\n" "$applicationUrl" >&2
    return 1
  fi
}

open_browser_when_ready() {
  local applicationUrl="$1" readinessUrl="${1%%\?*}health/ready" status attempt
  for ((attempt = 0; attempt < 120; attempt++)); do
    status="$(curl --silent --insecure --max-time 1 --output /dev/null --write-out '%{http_code}' "$readinessUrl")" || status=''
    if [[ "$status" =~ ^2[0-9][0-9]$ ]]; then
      if ! open_local_browser "$applicationUrl"; then
        printf "WARNING: The browser could not be opened. Open '%s' manually.\n" "$applicationUrl" >&2
      fi
      return 0
    fi
    sleep 0.5
  done
  printf "WARNING: MockAPI did not become ready at '%s'; the browser was not opened.\n" "$readinessUrl" >&2
}

invoke_run() {
  # Isolate the browser-worker cleanup trap from the interactive menu.
  (
    assert_dotnet
    require_command curl || die 'curl is required to wait for local application readiness.'
    local tutorialMode="${1:-skip}" applicationUrl browserPid status=0
    applicationUrl="$(get_local_application_url)?tutorial=$tutorialMode"
    open_browser_when_ready "$applicationUrl" &
    browserPid=$!
    trap 'kill "$browserPid" 2>/dev/null || true; wait "$browserPid" 2>/dev/null || true' EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    printf '\n'
    write_color cyan 'Running MockAPI'
    printf '\n'
    dotnet run --project "$REPOSITORY_ROOT/src/MockAPI/MockAPI.csproj" \
      --configuration "$CONFIGURATION" || status=$?
    (( status == 0 )) || die "Running MockAPI failed with exit code $status."
  )
}

# ---------------------------------------------------------------------------
# Tests / coverage / publish / validation.
# ---------------------------------------------------------------------------
invoke_tests() {
  assert_dotnet
  assert_pnpm
  run_tool "Running tests ($CONFIGURATION)" \
    dotnet test "$REPOSITORY_ROOT/MockAPI.slnx" --configuration "$CONFIGURATION" \
    -p:TreatWarningsAsErrors=true
  run_tool 'Running frontend unit tests' pnpm run test:frontend
  invoke_developer_cli_tests
}

invoke_developer_cli_tests() {
  local testFile="$REPOSITORY_ROOT/tests/DeveloperCli.Tests.ps1"
  [[ -f "$testFile" ]] || return 0
  require_command pwsh || die 'PowerShell 7 (pwsh) is required to run Developer CLI tests.'
  run_tool 'Running developer CLI tests' pwsh -NoProfile -File "$testFile"
}

invoke_coverage() {
  local coverageDirectory="$REPOSITORY_ROOT/artifacts/coverage"
  mkdir -p "$coverageDirectory"
  run_tool 'Running tests with XPlat code coverage' \
    dotnet test "$REPOSITORY_ROOT/MockAPI.slnx" \
    --configuration "$CONFIGURATION" \
    --settings "$REPOSITORY_ROOT/tests/coverage.runsettings" \
    '--collect:XPlat Code Coverage' \
    --results-directory "$coverageDirectory" \
    -p:TreatWarningsAsErrors=true

  local coverageFile
  coverageFile="$(find "$coverageDirectory" -type f -name coverage.cobertura.xml -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -n 1 | cut -d' ' -f2-)"
  [[ -n "$coverageFile" ]] || die "Tests passed but no Cobertura coverage file was found under '$coverageDirectory'."
  write_field 'Coverage report' "$coverageFile" green
  run_tool 'Validating backend coverage' pwsh -NoProfile -File \
    "$REPOSITORY_ROOT/scripts/Assert-Coverage.ps1" -Report "$coverageFile"
  run_tool 'Running frontend tests with coverage' pnpm run test:frontend:coverage
}

invoke_publish() {
  local publishDirectory="$REPOSITORY_ROOT/artifacts/publish"
  rm -rf "$publishDirectory"
  local runtime
  for runtime in linux-musl-x64 linux-musl-arm64; do
    local outputDirectory="$publishDirectory/$runtime"
    run_tool "Publishing $runtime ($CONFIGURATION)" \
      dotnet publish "$REPOSITORY_ROOT/src/MockAPI/MockAPI.csproj" \
      --configuration "$CONFIGURATION" \
      --runtime "$runtime" \
      --output "$outputDirectory" \
      -p:PublishAot=false \
      -p:TreatWarningsAsErrors=true
    run_tool "Validating publish contents for $runtime" pwsh -NoProfile -File \
      "$REPOSITORY_ROOT/scripts/Assert-PublishContents.ps1" \
      -PublishDirectory "$outputDirectory"
  done
}

invoke_pwsh_script() {
  # $1 = operation, $2 = repository-relative PowerShell script path.
  local operation="$1" scriptPath="$REPOSITORY_ROOT/$2"
  if ! require_command pwsh; then
    die "PowerShell 7 (pwsh) is required to run $2."
  fi
  run_tool "$operation" pwsh -NoProfile -File "$scriptPath"
}

invoke_dependency_security() {
  assert_pnpm
  run_tool 'Scanning dependency vulnerabilities' pnpm run validate:dependency-security
}

invoke_validation() {
  invoke_tooling_restore
  invoke_restore
  invoke_dependency_security
  invoke_lint
  invoke_build
  invoke_tests
  invoke_coverage
  invoke_publish
  printf '\n'
  write_color green 'Managed-code validation passed.'
}

invoke_all() {
  invoke_validation
  invoke_container_build
  printf '\n'
  write_color green 'Full local validation and native container build passed.'
}

invoke_initial_azure_pathway() {
  write_color cyan '1/3: Test'
  invoke_tests
  write_color cyan '2/3: Build'
  invoke_build
  write_color cyan '3/3: Provision and deploy'
  invoke_azure_up
}

invoke_update_azure_pathway() {
  write_color cyan '1/3: Test'
  invoke_tests
  write_color cyan '2/3: Build'
  invoke_build
  write_color cyan '3/3: Build, push, and deploy image'
  invoke_azure_deploy
}

# ---------------------------------------------------------------------------
# Container engine helpers.
# ---------------------------------------------------------------------------
assert_container_engine_available() {
  if [[ "$SKIP_CONTAINER_CHECK" == "true" ]]; then
    return 0
  fi
  assert_container_engine
}

container_json() {
  "$CONTAINER_ENGINE" inspect "$CONTAINER_NAME" 2>/dev/null
}

container_exists() {
  container_json >/dev/null 2>&1
}

container_running() {
  local json
  json="$(container_json)" || return 1
  MOCKAPI_JSON="$json" python3 -c '
import json,os,sys
data = json.loads(os.environ["MOCKAPI_JSON"])
sys.exit(0 if data and data[0].get("State", {}).get("Running") else 1)
'
}

get_container_recorded_image_id() {
  local json
  json="$(container_json)" || return 0
  MOCKAPI_JSON="$json" IMAGE_ID_LABEL="$IMAGE_ID_LABEL" python3 -c '
import json,os
data = json.loads(os.environ["MOCKAPI_JSON"])
label = os.environ["IMAGE_ID_LABEL"]
if not data:
    print("")
else:
    entry = data[0]
    labels = entry.get("Labels") or entry.get("Config", {}).get("Labels") or {}
    print(str(labels.get(label, "")).strip())
'
}

get_container_image_id() {
  local json
  if ! json="$("$CONTAINER_ENGINE" image inspect "$IMAGE_NAME" 2>/dev/null)"; then
    die "Container image '$IMAGE_NAME' does not exist. Run './start.sh --action container-build'."
  fi
  MOCKAPI_JSON="$json" python3 -c '
import json,os,sys
data = json.loads(os.environ["MOCKAPI_JSON"])
if not data or not str(data[0].get("Id", "")).strip():
    sys.exit(2)
print(str(data[0]["Id"]).strip())
' || die "Container image '$IMAGE_NAME' does not expose an image ID."
}

remove_container() {
  # Docker removes with "rm"; WSLC uses "remove".
  if [[ "$CONTAINER_ENGINE" == "docker" ]]; then
    "$CONTAINER_ENGINE" rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  else
    "$CONTAINER_ENGINE" remove --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  fi
}

volume_exists() {
  "$CONTAINER_ENGINE" volume inspect "$VOLUME_NAME" >/dev/null 2>&1
}

initialize_container_volume_permissions() {
  local maintenanceContainerName="${CONTAINER_NAME}-volume-init-$(printf '%08x' "$RANDOM$RANDOM")"
  run_tool "Initializing volume permissions for $VOLUME_NAME" \
    "$CONTAINER_ENGINE" run --name "$maintenanceContainerName" \
    --user root --entrypoint chown \
    "--volume=${VOLUME_NAME}:/data" \
    "$IMAGE_NAME" "$CONTAINER_USER" /data
  if [[ "$CONTAINER_ENGINE" == 'docker' ]]; then
    "$CONTAINER_ENGINE" rm --force "$maintenanceContainerName" >/dev/null
  else
    "$CONTAINER_ENGINE" remove --force "$maintenanceContainerName" >/dev/null
  fi
}

# ---------------------------------------------------------------------------
# Container build.
# ---------------------------------------------------------------------------
get_image_repository() {
  local finalSegment="${IMAGE_NAME##*/}"
  if [[ "$finalSegment" == *:* ]]; then
    printf '%s' "${IMAGE_NAME%:*}"
  else
    printf '%s' "$IMAGE_NAME"
  fi
}

new_build_image_name() {
  local timestamp="$1" suffix="${2:-}" tag
  tag="build-${timestamp}"
  [[ -z "$suffix" ]] || tag="${tag}-${suffix}"
  printf '%s:%s' "$(get_image_repository)" "$tag"
}

get_container_build_nuget_source() {
  local source="$NUGET_SOURCE"
  if [[ -z "$source" ]]; then
    source='https://api.nuget.org/v3/index.json'
    if require_command dotnet; then
      local enabledSource
      enabledSource="$(dotnet nuget list source --format Short 2>/dev/null | sed -nE 's/^E[[:space:]]+(https:\/\/[^[:space:]]+)[[:space:]]*$/\1/p' | head -n 1)"
      [[ -z "$enabledSource" ]] || source="$enabledSource"
    fi
  fi
  [[ "$source" =~ ^https://[^/@[:space:]]+([^@[:space:]]*)$ ]] || \
    die 'The NuGet source for container builds must be an absolute HTTPS URL without embedded credentials.'
  printf '%s' "$source"
}

add_build_tag_to_current_image() {
  local imageJson
  imageJson="$("$CONTAINER_ENGINE" image inspect "$IMAGE_NAME" 2>/dev/null)" || return 0
  MOCKAPI_JSON="$imageJson" MOCKAPI_REPOSITORY="$(get_image_repository)" python3 -c '
import datetime,json,os,subprocess
image = json.loads(os.environ["MOCKAPI_JSON"])[0]
prefix = os.environ["MOCKAPI_REPOSITORY"] + ":build-"
if any(str(tag).startswith(prefix) for tag in image.get("RepoTags") or []):
    raise SystemExit(0)
created = datetime.datetime.fromisoformat(str(image["Created"]).replace("Z", "+00:00"))
stamp = created.astimezone(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
short_id = str(image["Id"]).replace("sha256:", "")[:12]
print(f"{prefix}{stamp}-{short_id}")
' | while IFS= read -r preservedTag; do
    [[ -z "$preservedTag" ]] || run_tool "Preserving current image as $preservedTag" \
      "$CONTAINER_ENGINE" image tag "$IMAGE_NAME" "$preservedTag"
  done
}

invoke_container_build() {
  assert_container_engine_available
  local dockerfile="$REPOSITORY_ROOT/Dockerfile"
  [[ -f "$dockerfile" ]] || die "Dockerfile was not found at $dockerfile."
  local containerBuildNuGetSource
  containerBuildNuGetSource="$(get_container_build_nuget_source)"
  add_build_tag_to_current_image
  local buildImageName
  buildImageName="$(new_build_image_name "$(date -u +%Y%m%dT%H%M%SZ)")"
  write_field 'NuGet source' "$containerBuildNuGetSource"
  run_tool "Building native container image $buildImageName" \
    "$CONTAINER_ENGINE" build --pull --tag "$buildImageName" \
    --build-arg "NUGET_SOURCE=$containerBuildNuGetSource" "$REPOSITORY_ROOT"
  run_tool "Moving image alias $IMAGE_NAME to $buildImageName" \
    "$CONTAINER_ENGINE" image tag "$buildImageName" "$IMAGE_NAME"
  write_field 'Build image' "$buildImageName" green
  write_field 'Current alias' "$IMAGE_NAME" green
  write_color yellow 'Removing dashboard assets makes only a very small difference in image size.'
  write_color yellow 'Set MockApi__EnableDashboard=false to disable the dashboard when it should not be deployed.'
}

# ---------------------------------------------------------------------------
# Container run.
# ---------------------------------------------------------------------------
invoke_container_run() {
  assert_container_engine_available

  local imageId
  imageId="$(get_container_image_id)"

  if container_exists; then
    local recordedImageId
    recordedImageId="$(get_container_recorded_image_id)"
    if [[ -z "$recordedImageId" || "$recordedImageId" != "$imageId" ]]; then
      write_color yellow 'Existing container is stale; recreating it from the current image.'
      remove_container
    elif container_running; then
      write_color green "Container '$CONTAINER_NAME' is already running on the current image."
      invoke_container_test
      return 0
    else
      write_color cyan "Starting existing container '$CONTAINER_NAME'"
      run_tool 'Starting container' "$CONTAINER_ENGINE" start "$CONTAINER_NAME"
      invoke_container_test
      return 0
    fi
  fi

  if ! volume_exists; then
    run_tool "Creating volume '$VOLUME_NAME'" "$CONTAINER_ENGINE" volume create "$VOLUME_NAME"
  fi
  initialize_container_volume_permissions

  read_dashboard_credential

  local -a runArgs=(
    run --detach
    --name "$CONTAINER_NAME"
    --label "${IMAGE_ID_LABEL}=${imageId}"
    --publish "${PORT}:8080"
    "--volume=${VOLUME_NAME}:/data"
    --cpus 0.5
    --memory 256M
  )
  if [[ -n "$DASHBOARD_USERNAME" ]]; then
    runArgs+=(--env "MockApi__DashboardUsername=${DASHBOARD_USERNAME}")
    runArgs+=(--env "MockApi__DashboardPasswordHash=${DASHBOARD_PASSWORD_HASH}")
  fi
  runArgs+=("$IMAGE_NAME")

  run_tool "Starting container '$CONTAINER_NAME' ($CONTAINER_ENGINE)" \
    "$CONTAINER_ENGINE" "${runArgs[@]}"
  invoke_container_test
  write_field 'Application URL' "http://localhost:${PORT}" green
}

# ---------------------------------------------------------------------------
# Container test (readiness + dashboard smoke test).
# ---------------------------------------------------------------------------
invoke_container_test() {
  assert_container_engine_available
  container_exists || die "Container '$CONTAINER_NAME' does not exist. Run the container first."
  container_running || die "Container '$CONTAINER_NAME' is stopped. Run './start.sh --action container-run' to restart it."
  local baseUrl="http://localhost:${PORT}"

  local attempt readyBody statusCode ready="false"
  for attempt in $(seq 1 10); do
    statusCode="$(curl -s -o /tmp/mockapi-ready.$$ -w '%{http_code}' "${baseUrl}/health/ready" || true)"
    if [[ "$statusCode" == "200" ]]; then
      readyBody="$(cat /tmp/mockapi-ready.$$ 2>/dev/null || true)"
      if printf '%s' "$readyBody" | grep -q '"status":"ready"'; then
        ready="true"
        break
      fi
    fi
    sleep 0.5
  done
  rm -f /tmp/mockapi-ready.$$ 2>/dev/null || true

  if [[ "$ready" != "true" ]]; then
    die "Container smoke test failed for '${baseUrl}/health/ready' after 10 attempts. Review './start.sh --action container-logs'."
  fi
  write_field 'Smoke test' "${baseUrl}/health/ready -> HTTP 200" green

  local dashboardBody="/tmp/mockapi-dashboard.$$"
  local -a dashboardArgs=(-s -o "$dashboardBody" -w '%{http_code}')
  if [[ -n "$DASHBOARD_USERNAME" && -n "$DASHBOARD_PASSWORD" ]]; then
    dashboardArgs+=(-u "${DASHBOARD_USERNAME}:${DASHBOARD_PASSWORD}")
  fi
  local dashboardStatus
  dashboardStatus="$(curl "${dashboardArgs[@]}" "${baseUrl}/" || true)"
  if [[ "$dashboardStatus" == '401' && -z "$DASHBOARD_USERNAME" ]]; then
    rm -f "$dashboardBody"
    write_field 'Dashboard' "${baseUrl}/ -> HTTP 401 (authentication enabled)" green
    return
  fi
  if [[ "$dashboardStatus" != '200' ]] || ! grep -q '<title>MockAPI' "$dashboardBody"; then
    rm -f "$dashboardBody"
    die "Container smoke test expected the administrative dashboard at '${baseUrl}/'."
  fi
  rm -f "$dashboardBody"
  write_field 'Dashboard' "${baseUrl}/ -> HTTP 200" green
}

# ---------------------------------------------------------------------------
# Container showcase (loads and verifies the built-in example).
# ---------------------------------------------------------------------------
invoke_container_showcase() {
  local baseUrl="http://localhost:${PORT}"
  if [[ "$SKIP_CONTAINER_CHECK" != 'true' ]]; then
    assert_container_engine_available
  fi
  if [[ "$SKIP_CONTAINER_CHECK" != 'true' ]] && ! container_running; then
    die "Container '$CONTAINER_NAME' is not running. Run './start.sh --action container-run' first."
  fi

  local configurationUrl="${baseUrl}/__mockapi/api/configuration"
  local endpointsUrl="${baseUrl}/__mockapi/api/endpoints"
  local statisticsUrl="${baseUrl}/__mockapi/api/statistics"
  local exampleUrl="${baseUrl}/ex/rate-limited"
  local endpointId='7b2d425d-75f1-4ded-a74e-503374a7e99e'
  local headersFile="/tmp/mockapi-showcase-headers.$$" bodyFile="/tmp/mockapi-showcase-body.$$"

  local status
  status="$(curl -s --max-time 5 -o "$bodyFile" -w '%{http_code}' "$endpointsUrl" || true)"
  [[ "$status" != '401' ]] || die "Dashboard authentication is enabled. Load examples in the dashboard at $baseUrl, then rerun the showcase."
  [[ "$status" == '200' ]] || die "Reading the active endpoints returned HTTP $status."
  local hasEndpoint
  hasEndpoint="$(MOCKAPI_JSON="$(cat "$bodyFile")" MOCKAPI_ENDPOINT_ID="$endpointId" python3 -c '
import json,os
data = json.loads(os.environ["MOCKAPI_JSON"])
print("true" if any(str(entry.get("id", "")) == os.environ["MOCKAPI_ENDPOINT_ID"] for entry in data) else "false")
')"
  if [[ "$hasEndpoint" != 'true' ]]; then
    status="$(curl -s --max-time 5 -D "$headersFile" -o "$bodyFile" -w '%{http_code}' "$configurationUrl" || true)"
    [[ "$status" != '401' ]] || die "Dashboard authentication is enabled. Load examples in the dashboard at $baseUrl, then rerun the showcase."
    local etag
    etag="$(grep -i '^ETag:' "$headersFile" | head -n 1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//')"
    [[ "$status" == '200' && -n "$etag" ]] || die 'The management API did not return the current configuration ETag.'
    status="$(curl -s --max-time 5 -X POST -H "If-Match: $etag" -o "$bodyFile" -w '%{http_code}' "${configurationUrl}/example/merge" || true)"
    [[ "$status" != '409' ]] || die "The built-in examples conflict with the active configuration. Resolve the conflicts from the dashboard at $baseUrl, then rerun the showcase."
    [[ "$status" == '200' ]] || die "Loading the built-in examples returned HTTP $status."
    write_field 'Examples' 'Loaded built-in examples' green
  fi

  local statsBefore totalBefore matchedBefore unmatchedBefore endpointBefore
  statsBefore="$(curl -s --max-time 5 "$statisticsUrl")" || die "Reading statistics from '$statisticsUrl' failed."
  read -r totalBefore matchedBefore unmatchedBefore endpointBefore < <(
    MOCKAPI_JSON="$statsBefore" MOCKAPI_ENDPOINT_ID="$endpointId" python3 -c '
import json,os
data = json.loads(os.environ["MOCKAPI_JSON"])
endpointId = os.environ["MOCKAPI_ENDPOINT_ID"]
total = int(data.get("totalRequests", 0))
matched = int(data.get("matchedRequests", 0))
unmatched = int(data.get("unmatchedRequests", 0))
endpoint = 0
for entry in data.get("endpoints", []):
    if str(entry.get("endpointId", "")) == endpointId:
        endpoint = int(entry.get("totalRequests", 0))
        break
print(total, matched, unmatched, endpoint)
' || die 'The statistics response was invalid.'
  )

  local rateLimitRequestCount=0 attempt
  for attempt in 1 2 3 4 5; do
    status="$(curl --http1.1 -s --max-time 5 -D "$headersFile" -o "$bodyFile" -w '%{http_code}' "$exampleUrl" || true)"
    (( rateLimitRequestCount += 1 ))
    [[ "$status" != '404' ]] || die 'The built-in rate-limit example is unavailable after loading the examples.'
    [[ "$status" != '429' ]] || break
  done

  local statusLine reasonPhrase retryAfter contentType body
  statusLine="$(head -n 1 "$headersFile" 2>/dev/null | tr -d '\r')"
  reasonPhrase="$(printf '%s' "$statusLine" | sed -E 's|^HTTP/[0-9.]+ [0-9]+ ?||')"
  retryAfter="$(grep -i '^Retry-After:' "$headersFile" 2>/dev/null | head -n 1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//')"
  contentType="$(grep -i '^Content-Type:' "$headersFile" 2>/dev/null | head -n 1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//')"
  body="$(cat "$bodyFile" 2>/dev/null || true)"

  local mockSources
  mockSources="$(grep -i '^X-Mock-Source:' "$headersFile" 2>/dev/null | cut -d: -f2- | tr -d '\r' | sed 's/^ *//' | paste -sd, -)"
  [[ "$status" == "429" ]] || die "Expected HTTP 429 from the example endpoint but received $status."
  [[ "$reasonPhrase" == "Too Many Requests" ]] || die "Expected reason phrase 'Too Many Requests' but received '$reasonPhrase'."
  [[ "$retryAfter" == "10" ]] || die "Expected Retry-After: 10 but received '$retryAfter'."
  [[ "$contentType" == "application/json; charset=utf-8" ]] || die "Expected content type 'application/json; charset=utf-8' but received '$contentType'."
  [[ "$body" == '{"error":"try again later"}' ]] || die "Unexpected response body: $body"
  [[ "$mockSources" == *MockAPI* && "$mockSources" == *checked-in-example* ]] || die "Expected repeated X-Mock-Source values but received '$mockSources'."

  [[ "$(curl -s --max-time 5 -o /dev/null -w '%{http_code}' "${exampleUrl}?request=showcase" || true)" == '429' ]] || die 'Query-insensitive match assertion failed.'
  [[ "$(curl -s --max-time 5 -X POST -o /dev/null -w '%{http_code}' "$exampleUrl" || true)" == '404' ]] || die 'Unsupported method assertion failed.'
  [[ "$(curl -s --max-time 5 -o /dev/null -w '%{http_code}' "${baseUrl}/ex/not-configured" || true)" == '404' ]] || die 'Unmatched path assertion failed.'

  local statsAfter
  statsAfter="$(curl -s --max-time 5 "$statisticsUrl")" || die "Reading statistics from '$statisticsUrl' failed."
  local totalAfter matchedAfter unmatchedAfter endpointAfter
  read -r totalAfter matchedAfter unmatchedAfter endpointAfter < <(
    MOCKAPI_JSON="$statsAfter" MOCKAPI_ENDPOINT_ID="$endpointId" python3 -c '
import json,os
data = json.loads(os.environ["MOCKAPI_JSON"])
endpointId = os.environ["MOCKAPI_ENDPOINT_ID"]
total = int(data.get("totalRequests", 0))
matched = int(data.get("matchedRequests", 0))
unmatched = int(data.get("unmatchedRequests", 0))
endpoint = 0
for entry in data.get("endpoints", []):
    if str(entry.get("endpointId", "")) == endpointId:
        endpoint = int(entry.get("totalRequests", 0))
        break
print(total, matched, unmatched, endpoint)
' || die 'The statistics response was invalid.'
  )

  local expectedTotalRequests=$((rateLimitRequestCount + 3))
  local expectedMatchedRequests=$((rateLimitRequestCount + 1))
  (( totalAfter - totalBefore == expectedTotalRequests )) || die "Expected total requests to increase by $expectedTotalRequests (before $totalBefore, after $totalAfter)."
  (( matchedAfter - matchedBefore == expectedMatchedRequests )) || die "Expected matched requests to increase by $expectedMatchedRequests (before $matchedBefore, after $matchedAfter)."
  (( unmatchedAfter - unmatchedBefore == 2 )) || die "Expected unmatched requests to increase by 2 (before $unmatchedBefore, after $unmatchedAfter)."
  (( endpointAfter - endpointBefore == expectedMatchedRequests )) || die "Expected example endpoint requests to increase by $expectedMatchedRequests (before $endpointBefore, after $endpointAfter)."

  rm -f "$headersFile" "$bodyFile"
  printf '\n'
  write_color cyan 'Built-in example showcase'
  write_field '429 status' 'PASS' green
  write_field 'Reason phrase' 'PASS' green
  write_field 'Retry-After' 'PASS' green
  write_field 'Repeated headers' 'PASS' green
  write_field 'Content type' 'PASS' green
  write_field 'Exact body' 'PASS' green
  write_field 'Query-insensitive match' 'PASS' green
  write_field 'Unsupported method' 'PASS' green
  write_field 'Unmatched path' 'PASS' green
  write_field 'Aggregate statistics' 'PASS' green
  write_field 'Endpoint statistics' 'PASS' green
  write_field 'Showcase' 'All assertions passed' green
}

# ---------------------------------------------------------------------------
# Container lifecycle helpers.
# ---------------------------------------------------------------------------
invoke_container_logs() {
  assert_container_engine_available
  run_tool "Showing logs for $CONTAINER_NAME" "$CONTAINER_ENGINE" logs --tail 200 "$CONTAINER_NAME"
}

invoke_container_status() {
  assert_container_engine_available
  run_tool "Inspecting $CONTAINER_NAME" "$CONTAINER_ENGINE" inspect "$CONTAINER_NAME"
}

invoke_container_stop() {
  assert_container_engine_available
  run_tool "Stopping $CONTAINER_NAME" "$CONTAINER_ENGINE" stop --time 1 "$CONTAINER_NAME"
}

invoke_container_remove() {
  assert_container_engine_available
  if should_process "$CONTAINER_NAME" "Remove $CONTAINER_ENGINE container"; then
    if [[ "$CONTAINER_ENGINE" == 'docker' ]]; then
      run_tool "Removing $CONTAINER_NAME" "$CONTAINER_ENGINE" rm "$CONTAINER_NAME"
    else
      run_tool "Removing $CONTAINER_NAME" "$CONTAINER_ENGINE" remove "$CONTAINER_NAME"
    fi
  fi
}

# ---------------------------------------------------------------------------
# Dashboard credential capture and PBKDF2-SHA256 hashing.
# ---------------------------------------------------------------------------
read_dashboard_credential() {
  printf '\n'
  local username
  read -r -p 'Dashboard username (leave blank to disable authentication): ' username || true
  username="$(trim "$username")"
  if [[ -z "$username" ]]; then
    DASHBOARD_USERNAME=""
    DASHBOARD_PASSWORD=""
    DASHBOARD_PASSWORD_HASH=""
    write_color yellow 'Dashboard authentication is disabled for this container.'
    return 0
  fi
  case "$username" in
    *:*) die 'Dashboard username cannot contain a colon.' ;;
  esac

  local password confirmation
  read -rs -p 'Dashboard password (minimum 8 characters): ' password || true
  printf '\n'
  read -rs -p 'Confirm dashboard password: ' confirmation || true
  printf '\n'

  [[ "${#password}" -ge 8 ]] || die 'Dashboard password must contain at least 8 characters.'
  [[ "$password" == "$confirmation" ]] || die 'Dashboard passwords do not match.'

  DASHBOARD_USERNAME="$username"
  DASHBOARD_PASSWORD="$password"
  DASHBOARD_PASSWORD_HASH="$(new_dashboard_password_hash "$password")"
  write_color green 'Dashboard authentication is enabled for this container.'
}

new_dashboard_password_hash() {
  # Mirrors the PBKDF2-SHA256 format used by start.ps1:
  # v1.{iterations}.{base64(salt)}.{base64(hash)} with 600000 iterations,
  # a 16-byte salt, and a 32-byte derived key.
  require_command python3 || die 'python3 is required to hash the dashboard password.'
  MOCKAPI_PASSWORD="$1" python3 -c '
import base64,hashlib,os
password = os.environ["MOCKAPI_PASSWORD"].encode("utf-8")
iterations = 600000
salt = os.urandom(16)
derived = hashlib.pbkdf2_hmac("sha256", password, salt, iterations, 32)
print("v1.%d.%s.%s" % (
    iterations,
    base64.b64encode(salt).decode("ascii"),
    base64.b64encode(derived).decode("ascii"),
))
'
}

# ---------------------------------------------------------------------------
# Azure tooling setup and checks.
# ---------------------------------------------------------------------------
invoke_azure_setup() {
  assert_azd
  assert_azure_cli
  printf '\n'
  write_color green 'Azure tooling is ready.'
}

version_at_least() {
  local actual="$1" required="$2"
  [[ "$(printf '%s\n%s\n' "$required" "$actual" | sort -V | head -n 1)" == "$required" ]]
}

assert_azd() {
  if ! require_command azd; then
    [[ "$INSTALL_MISSING" == 'true' ]] || die "Azure Developer CLI is missing. Run './start.sh --action azure-setup --install-missing'."
    install_azd
  fi
  require_command azd || die 'Azure Developer CLI is still unavailable after installation. Restart the terminal and run Azure tooling setup again.'
  local versionText version
  versionText="$(azd version 2>/dev/null)" || die 'Unable to resolve the Azure Developer CLI version.'
  version="$(printf '%s' "$versionText" | sed -nE 's/.*azd version ([0-9]+\.[0-9]+\.[0-9]+).*/\1/p' | head -n 1)"
  [[ -n "$version" ]] || die 'Unable to resolve the Azure Developer CLI version.'
  if ! version_at_least "$version" "$MINIMUM_AZD_VERSION" && [[ "$INSTALL_MISSING" == 'true' ]]; then
    install_azd
    versionText="$(azd version 2>/dev/null)" || die 'Unable to resolve the Azure Developer CLI version after installation.'
    version="$(printf '%s' "$versionText" | sed -nE 's/.*azd version ([0-9]+\.[0-9]+\.[0-9]+).*/\1/p' | head -n 1)"
  fi
  version_at_least "$version" "$MINIMUM_AZD_VERSION" || die "Azure Developer CLI $MINIMUM_AZD_VERSION or newer is required, but $version is installed."
  write_field 'Azure Dev CLI' "$version" green
}

assert_azure_cli() {
  if ! require_command az; then
    [[ "$INSTALL_MISSING" == 'true' ]] || die "Azure CLI is missing. Run './start.sh --action azure-setup --install-missing'."
    install_azure_cli
  fi
  require_command az || die 'Azure CLI is still unavailable after installation. Restart the terminal and run Azure tooling setup again.'
  local versionJson version
  versionJson="$(az version --output json 2>/dev/null)" || die 'Unable to resolve the Azure CLI version.'
  version="$(MOCKAPI_JSON="$versionJson" python3 -c 'import json,os; print(json.loads(os.environ["MOCKAPI_JSON"]).get("azure-cli", ""))')" || die 'Unable to parse the Azure CLI version.'
  [[ -n "$version" ]] || die 'Unable to resolve the Azure CLI version.'
  write_field 'Azure CLI' "$version" green
}

get_azure_deployment_configuration() {
  local envPath="$ENVIRONMENT_FILE"
  if [[ "$envPath" =~ ^[A-Za-z]:[\\/] ]] && require_command cygpath; then
    envPath="$(cygpath -u "$envPath")"
  elif [[ "$envPath" != /* ]]; then
    envPath="$REPOSITORY_ROOT/$envPath"
  fi
  [[ -f "$envPath" ]] || die "Azure settings were not found at '$envPath'. Copy '.env.example' to '.env' and set AZURE_SUBSCRIPTION_ID."

  MOCKAPI_ENV_PATH="$envPath" python3 -c '
import os,re,sys

path = os.environ["MOCKAPI_ENV_PATH"]
values = {}
with open(path, "r", encoding="utf-8") as handle:
    for line_number, raw in enumerate(handle, 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        match = re.fullmatch(r"([A-Z][A-Z0-9_]*)=(.*)", line)
        if not match:
            sys.exit(f"Malformed environment entry at {path}:{line_number}. Expected KEY=value.")
        key, value = match.groups()
        if key not in {"AZURE_SUBSCRIPTION_ID", "AZURE_LOCATION", "AZURE_ENV_NAME", "AZURE_CUSTOM_DOMAIN", "AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD", "AZURE_DASHBOARD_USERNAME", "AZURE_DASHBOARD_PASSWORD"}:
            sys.exit(f"Unknown Azure deployment key {key!r} at {path}:{line_number}.")
        if key in values:
            sys.exit(f"Duplicate Azure deployment key {key!r} at {path}:{line_number}.")
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {"\"", "\x27"}:
            value = value[1:-1]
        elif "\"" in value or "\x27" in value:
            sys.exit(f"Malformed quoted value for {key!r} at {path}:{line_number}.")
        values[key] = value

def fail(message):
    sys.stderr.write(message + "\n")
    sys.exit(1)

def resolve(key):
    return os.environ.get(key, "").strip() or values.get(key, "").strip()

subscription = resolve("AZURE_SUBSCRIPTION_ID")
location = resolve("AZURE_LOCATION")
environmentName = resolve("AZURE_ENV_NAME")

for key, value in (("AZURE_SUBSCRIPTION_ID", subscription), ("AZURE_LOCATION", location), ("AZURE_ENV_NAME", environmentName)):
    if not value:
        fail(f"Azure deployment setting {key!r} is required. Set it in the process environment or {path!r}.")

if not re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", subscription):
    fail("Azure deployment setting AZURE_SUBSCRIPTION_ID must be a GUID.")
if not re.fullmatch(r"[a-z0-9]+", location):
    fail("Azure deployment setting AZURE_LOCATION must use an Azure CLI location name such as eastus2.")
if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9-]{0,63}", environmentName):
    fail("Azure deployment setting AZURE_ENV_NAME must contain 1-64 letters, numbers, or hyphens and cannot start with a hyphen.")

dashboardUsername = resolve("AZURE_DASHBOARD_USERNAME")
dashboardPassword = resolve("AZURE_DASHBOARD_PASSWORD")
domain = resolve("AZURE_CUSTOM_DOMAIN").lower()
method = resolve("AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD") or "CNAME"
if domain and (len(domain) > 253 or not re.fullmatch(r"(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?", domain)):
    fail("Azure deployment setting AZURE_CUSTOM_DOMAIN must be a DNS hostname, without a scheme, path, port, wildcard, or trailing dot.")
if method not in {"CNAME", "HTTP"}:
    fail("Azure deployment setting AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD must be CNAME (subdomain) or HTTP (apex domain).")

if bool(dashboardUsername) != bool(dashboardPassword):
    fail("Azure deployment settings AZURE_DASHBOARD_USERNAME and AZURE_DASHBOARD_PASSWORD must both be set or both be empty.")
if dashboardUsername:
    if ":" in dashboardUsername:
        fail("Azure deployment setting AZURE_DASHBOARD_USERNAME cannot contain a colon.")
    if len(dashboardPassword) < 8:
        fail("Azure deployment setting AZURE_DASHBOARD_PASSWORD must contain at least 8 characters.")

print("AZURE_SUBSCRIPTION_ID=" + subscription)
print("AZURE_LOCATION=" + location)
print("AZURE_ENV_NAME=" + environmentName)
print("AZURE_CUSTOM_DOMAIN=" + domain)
print("AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=" + method)
print("AZURE_DASHBOARD_USERNAME=" + dashboardUsername)
print("AZURE_DASHBOARD_PASSWORD=" + dashboardPassword)
' || die "The Azure settings file '$envPath' is invalid."
}

invoke_azure_check() {
  load_azure_configuration
  assert_azd
  assert_azure_authentication
  set_azd_environment "$AZURE_CONFIGURATION"
  run_tool 'Validating Azure Developer CLI project' azd show \
    --environment "$AZURE_CONFIGURATION_ENVIRONMENT_NAME" --no-prompt
  assert_azure_infrastructure
  write_field 'Azure environment' "$AZURE_CONFIGURATION_ENVIRONMENT_NAME"
  write_field 'Azure subscription' "$AZURE_CONFIGURATION_SUBSCRIPTION"
  write_field 'Azure location' "$AZURE_CONFIGURATION_LOCATION"
  printf '\n'
  write_color green 'Azure deployment preflight passed.'
}

configuration_value() {
  printf '%s\n' "$1" | sed -n "s|^$2=||p"
}

load_azure_configuration() {
  AZURE_CONFIGURATION="$(get_azure_deployment_configuration)"
  AZURE_CONFIGURATION_SUBSCRIPTION="$(configuration_value "$AZURE_CONFIGURATION" AZURE_SUBSCRIPTION_ID)"
  AZURE_CONFIGURATION_LOCATION="$(configuration_value "$AZURE_CONFIGURATION" AZURE_LOCATION)"
  AZURE_CONFIGURATION_ENVIRONMENT_NAME="$(configuration_value "$AZURE_CONFIGURATION" AZURE_ENV_NAME)"
}

assert_azure_authentication() {
  if ! azd auth login --check-status --no-prompt >/dev/null 2>&1; then
    write_color yellow 'Azure authentication is required. Starting interactive azd login.'
    run_tool 'Authenticating Azure Developer CLI' azd auth login
    azd auth login --check-status --no-prompt >/dev/null 2>&1 || die 'Azure Developer CLI authentication could not be verified after login.'
  fi
  write_field 'Azure authentication' 'Authenticated' green
}

assert_azure_infrastructure() {
  assert_azure_cli
  mkdir -p "$REPOSITORY_ROOT/artifacts/azure"
  run_tool 'Building Azure Bicep infrastructure' az bicep build \
    --file "$REPOSITORY_ROOT/infra/main.bicep" \
    --outfile "$REPOSITORY_ROOT/artifacts/azure/main.json"
}

set_azd_environment() {
  # Selects or creates the azd environment and applies the required settings.
  local configuration="$1"
  local subscription location environmentName dashboardUsername dashboardPassword
  subscription="$(printf '%s\n' "$configuration" | sed -n 's|^AZURE_SUBSCRIPTION_ID=||p')"
  location="$(printf '%s\n' "$configuration" | sed -n 's|^AZURE_LOCATION=||p')"
  environmentName="$(printf '%s\n' "$configuration" | sed -n 's|^AZURE_ENV_NAME=||p')"
  dashboardUsername="$(configuration_value "$configuration" AZURE_DASHBOARD_USERNAME)"
  dashboardPassword="$(configuration_value "$configuration" AZURE_DASHBOARD_PASSWORD)"

  if ! azd env select "$environmentName" --no-prompt >/dev/null 2>&1; then
    run_tool "Creating azd environment '$environmentName'" \
      azd env new "$environmentName" --subscription "$subscription" --location "$location" --no-prompt
  fi

  run_tool 'Setting azd environment value AZURE_SUBSCRIPTION_ID' azd env set AZURE_SUBSCRIPTION_ID "$subscription" --environment "$environmentName" --no-prompt
  run_tool 'Setting azd environment value AZURE_LOCATION' azd env set AZURE_LOCATION "$location" --environment "$environmentName" --no-prompt
  run_tool 'Setting azd environment value AZURE_ENV_NAME' azd env set AZURE_ENV_NAME "$environmentName" --environment "$environmentName" --no-prompt

  local hash=''
  if [[ -n "$dashboardUsername" ]]; then
    hash="$(new_dashboard_password_hash "$dashboardPassword")"
    run_tool 'Setting azd environment value AZURE_DASHBOARD_USERNAME' azd env set AZURE_DASHBOARD_USERNAME "$dashboardUsername" --environment "$environmentName" --no-prompt
    run_tool 'Setting azd environment value AZURE_DASHBOARD_PASSWORD_HASH' azd env set AZURE_DASHBOARD_PASSWORD_HASH "$hash" --environment "$environmentName" --no-prompt
  else
    run_tool 'Clearing azd environment value AZURE_DASHBOARD_USERNAME' azd env set AZURE_DASHBOARD_USERNAME= --environment "$environmentName" --no-prompt
    run_tool 'Clearing azd environment value AZURE_DASHBOARD_PASSWORD_HASH' azd env set AZURE_DASHBOARD_PASSWORD_HASH= --environment "$environmentName" --no-prompt
  fi
}

get_azd_environment_value() {
  local environmentName="$1" name="$2" value
  value="$(azd env get-value "$name" --environment "$environmentName" --no-prompt 2>/dev/null)" || die "Azure environment value '$name' was not available in azd environment '$environmentName'."
  value="$(trim "$value")"
  value="${value%\"}"; value="${value#\"}"
  [[ -n "$value" ]] || die "Azure environment value '$name' was empty in azd environment '$environmentName'."
  printf '%s' "$value"
}

get_azure_service_uri() {
  local environmentName="$1" uri
  uri="$(get_azd_environment_value "$environmentName" SERVICE_MOCKAPI_URI)"
  uri="$(trim "$uri")"
  if [[ ! "$uri" =~ ^https?:// ]]; then
    die 'The Container App service URI was not available from azd. Ensure provisioning completed.'
  fi
  printf '%s' "${uri%/}"
}

write_azure_service_summary() {
  local serviceUri="$1"
  printf '\n'
  write_color green 'All done! The Azure action completed successfully.'
  printf '\n'
  printf '%s\n' 'Dashboard URL:'
  write_color green "${serviceUri%/}/"
}

test_azure_deployment() {
  local environmentName="$1" serviceUri
  serviceUri="${2:-}"
  printf '\n'
  write_color cyan 'Next: verify Azure deployment readiness. Waiting for the app to start may take a few minutes.'
  if [[ -z "$serviceUri" ]]; then
    serviceUri="$(get_azure_service_uri "$environmentName")"
  fi
  AZURE_SERVICE_URI=""
  local attempt statusCode body
  for attempt in $(seq 1 6); do
    write_color cyan "Readiness check ($attempt/6): ${serviceUri}/health/ready"
    statusCode="$(curl -s -o /tmp/mockapi-azure-ready.$$ -w '%{http_code}' "${serviceUri}/health/ready" || true)"
    if [[ "$statusCode" == "200" ]]; then
      body="$(cat /tmp/mockapi-azure-ready.$$ 2>/dev/null || true)"
      if printf '%s' "$body" | grep -q '"status":"ready"'; then
        rm -f /tmp/mockapi-azure-ready.$$ 2>/dev/null || true
        write_field 'Azure smoke test' "${serviceUri}/health/ready -> HTTP 200" green
        AZURE_SERVICE_URI="$serviceUri"
        return
      fi
    fi
    if (( attempt < 6 )); then
      write_color yellow 'Not ready yet; retrying in 5 seconds.'
      sleep 5
    fi
  done
  rm -f /tmp/mockapi-azure-ready.$$ 2>/dev/null || true
  die "The deployment did not become ready at ${serviceUri}/health/ready."
}

get_azure_cli_value() {
  local value
  value="$(az "$@")" || die 'Azure CLI failed while reading custom-domain deployment state.'
  trim "$value"
}

get_azure_container_app_id() {
  local resourceGroup="rg-${AZURE_CONFIGURATION_ENVIRONMENT_NAME}-${AZURE_CONFIGURATION_LOCATION}"
  # A subscription-level list also succeeds before the resource group exists.
  get_azure_cli_value resource list --subscription "$AZURE_CONFIGURATION_SUBSCRIPTION" \
    --tag azd-service-name=mockapi \
    --query "[?resourceGroup=='$resourceGroup' && type=='Microsoft.App/containerApps'].id | [0]" \
    --output tsv
}

save_azure_domain_bindings() {
  local appId bindings='[]' value
  appId="$(get_azure_container_app_id)" || return 1
  if [[ -n "$appId" ]]; then
    value="$(get_azure_cli_value containerapp show --ids "$appId" \
      --query properties.configuration.ingress.customDomains --output json)" || return 1
    bindings="$(printf '%s' "$value" | python3 -c '
import json,sys
value = json.load(sys.stdin)
if value is not None and not isinstance(value, list):
    sys.exit("Azure returned an invalid custom-domain bindings array.")
print(json.dumps(value if value is not None else [], separators=(",", ":")))
')" || return 1
  fi
  run_tool 'Preserving existing custom-domain bindings' azd env set AZURE_CUSTOM_DOMAINS "$bindings" \
    --environment "$AZURE_CONFIGURATION_ENVIRONMENT_NAME" --no-prompt
}

invoke_azure_custom_domain_command() {
  local operation="$1" output exitCode
  shift
  printf '\n'
  write_color cyan "$operation"
  set +e
  output="$(az "$@" 2>&1)"
  exitCode=$?
  set -e
  if (( exitCode == 0 )); then
    [[ -z "$output" ]] || printf '%s\n' "$output"
    return 0
  fi
  if [[ "$output" == *InvalidCustomHostNameValidation* ]]; then
    write_field 'Custom domain' 'Pending DNS validation (expected until DNS is configured and propagated). Complete steps 1-4 above; the generated Azure endpoint remains available.' yellow
    AZURE_CUSTOM_DOMAIN_PENDING=true
    return 0
  fi
  [[ -z "$output" ]] || printf '%s\n' "$output" >&2
  die "$operation failed with exit code $exitCode."
}

set_azure_custom_domain() {
  local domain method appId app environmentId fqdn verificationId bindingType certificateId appName
  domain="$(configuration_value "$AZURE_CONFIGURATION" AZURE_CUSTOM_DOMAIN)"
  [[ -n "$domain" ]] || return 0
  printf '\n'
  write_color cyan 'Next: check custom-domain configuration and Azure-managed certificate status.'
  method="$(configuration_value "$AZURE_CONFIGURATION" AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD)"
  appId="$(get_azure_container_app_id)" || return 1
  [[ -n "$appId" ]] || die 'MockAPI Container App was not found. Provision the environment before configuring a custom domain.'
  app="$(get_azure_cli_value containerapp show --ids "$appId" --output json)" || return 1
  local details
  details="$(printf '%s' "$app" | python3 -c '
import json,sys
app = json.load(sys.stdin)
properties = app["properties"]
ingress = properties["configuration"]["ingress"]
environment = properties.get("environmentId") or properties.get("managedEnvironmentId")
fqdn = ingress.get("fqdn")
verification = properties.get("customDomainVerificationId")
if not environment or not fqdn or not verification:
    sys.exit("Azure returned incomplete Container App domain-validation information.")
binding = next((b for b in ingress.get("customDomains") or [] if b["name"].lower() == sys.argv[1]), {})
for key,value in (("NAME", app["name"]), ("ENVIRONMENT", environment), ("FQDN", fqdn),
                  ("VERIFICATION", verification), ("BINDING", binding.get("bindingType") or ""),
                  ("CERTIFICATE", binding.get("certificateId") or "")):
    print(key + "=" + value)
' "$domain")" || return 1
  appName="$(configuration_value "$details" NAME)"
  environmentId="$(configuration_value "$details" ENVIRONMENT)"
  fqdn="$(configuration_value "$details" FQDN)"
  verificationId="$(configuration_value "$details" VERIFICATION)"
  bindingType="$(configuration_value "$details" BINDING)"
  certificateId="$(configuration_value "$details" CERTIFICATE)"
  if [[ "$bindingType" == 'SniEnabled' ]]; then
    [[ "${certificateId,,}" == */managedcertificates/* ]] || die 'The configured domain already uses a non-managed certificate. Remove that binding explicitly before switching to an Azure-managed certificate.'
    write_field 'Custom domain' "$domain already uses an Azure-managed certificate."
    AZURE_CUSTOM_DOMAIN_READY=true
    return
  fi
  local dnsNameWidth=$(( ${#domain} + 6 )) dnsName
  printf -v dnsName '%-*s' "$dnsNameWidth" "$domain"
  printf '\n'
  printf '%s\n' "Custom domain setup: $domain"
  printf '%s\n' 'DNS is managed by you. If these records are already configured, no DNS changes are needed.'
  printf '\n'
  write_color yellow 'Yellow = action required from you. Cyan = exact DNS entry or command to copy.'
  printf '\n'
  if [[ "$method" == 'HTTP' ]]; then
    local ip
    ip="$(get_azure_cli_value containerapp env show --ids "$environmentId" --query properties.staticIp --output tsv)" || return 1
    [[ -n "$ip" ]] || die 'Azure did not return the Container Apps environment IP address.'
    write_color yellow '1) ACTION REQUIRED: Set up the A record at your DNS provider.'
    printf '\n'
    write_field '   DNS A' "$dnsName -> $ip" cyan
    printf '\n'
    printf '%s\n' '   Why: routes the apex hostname to the Container Apps environment IP for HTTP validation.'
  else
    write_color yellow '1) ACTION REQUIRED: Set up the CNAME record at your DNS provider.'
    printf '\n'
    write_field '   DNS CNAME' "$dnsName -> $fqdn" cyan
    printf '\n'
    printf '%s\n' '   Why: routes the subdomain directly to this Container App so Azure can validate it.'
  fi
  printf '%s\n' '   Use direct DNS (no proxy or intermediate CNAME).'
  printf '\n'
  write_color yellow '2) ACTION REQUIRED: Set up the TXT record at your DNS provider.'
  printf '\n'
  write_field '   DNS TXT' "asuid.$domain -> $verificationId" cyan
  printf '\n'
  printf '%s\n' '   Why: proves that you control the hostname before Azure accepts the custom domain.'
  printf '%s\n' '   Record names above are fully qualified; your provider may require names relative to your DNS zone.'
  printf '\n'
  write_color cyan '3) OPTIONAL CHECK: Update CAA only if your domain restricts certificate issuers.'
  printf '\n'
  printf '%s\n' '   At the applicable DNS zone, allow: 0 issue "digicert.com" (flags: 0; tag: issue; value: digicert.com).'
  printf '%s\n' '   Do not place CAA at a CNAME name; use the applicable parent zone instead.'
  printf '%s\n' '   Why: authorizes DigiCert to issue and renew the free Azure-managed certificate.'
  printf '%s\n' '   If no CAA policy restricts issuers, no CAA change is required. Preserve other required issuer entries.'
  printf '\n'
  if [[ "$ACTION" == 'menu' ]]; then
    write_color yellow '4) ACTION REQUIRED: Wait for DNS propagation, then press 4 at the next prompt to continue.'
    printf '\n'
    printf '%s\n' '   Why: retries only custom-domain setup and HTTPS verification; it does not rebuild or redeploy.'
    printf '%s\n' '   Any other key returns to the menu without clearing these instructions.'
  else
    write_color yellow '4) ACTION REQUIRED: Wait for DNS propagation, then rerun either command from the repository root:'
    printf '\n'
    local powerShellEnvironmentFile="${ENVIRONMENT_FILE//\'/\'\'}"
    local bashEnvironmentFile="${ENVIRONMENT_FILE//\'/\'\\\'\'}"
    write_color cyan "   PowerShell: .\\start.ps1 -Action azure-deploy -EnvironmentFile '$powerShellEnvironmentFile'"
    write_color cyan "   Bash:       ./start.sh --action azure-deploy --environment-file '$bashEnvironmentFile'"
    printf '%s\n' '   Keep the same process-environment overrides, if any.'
    printf '%s\n' '   Why: redeploys the app, automatically adds the custom hostname, issues and binds the certificate,'
    printf '%s\n' '   then verifies HTTPS readiness. Certificate issuance can take several minutes.'
  fi
  printf '%s\n' '   Keep the DNS records and public ingress available for automatic certificate renewal.'
  local resourceGroup="rg-${AZURE_CONFIGURATION_ENVIRONMENT_NAME}-${AZURE_CONFIGURATION_LOCATION}"
  local arguments=(--name "$appName" --resource-group "$resourceGroup" \
    --subscription "$AZURE_CONFIGURATION_SUBSCRIPTION" --hostname "$domain")
  if [[ -z "$bindingType" ]]; then
    invoke_azure_custom_domain_command 'Checking custom hostname validation (pending DNS is expected on first deployment)' \
      containerapp hostname add "${arguments[@]}"
    [[ "$AZURE_CUSTOM_DOMAIN_PENDING" == true ]] && return 0
  fi
  invoke_azure_custom_domain_command 'Issuing and binding Azure-managed certificate (this can take several minutes)' \
    containerapp hostname bind "${arguments[@]}" --environment "$environmentId" --validation-method "$method"
  [[ "$AZURE_CUSTOM_DOMAIN_PENDING" == true ]] || AZURE_CUSTOM_DOMAIN_READY=true
}

complete_azure_deployment() {
  test_azure_deployment "$AZURE_CONFIGURATION_ENVIRONMENT_NAME"
  AZURE_CUSTOM_DOMAIN_PENDING=false
  AZURE_CUSTOM_DOMAIN_READY=false
  set_azure_custom_domain
  while [[ "$AZURE_CUSTOM_DOMAIN_PENDING" == true && "$ACTION" == 'menu' ]]; do
    DOMAIN_CONTINUATION_PROMPTED=true
    read_azure_domain_continuation || break
    AZURE_CUSTOM_DOMAIN_PENDING=false
    set_azure_custom_domain
  done
  local domain
  domain="$(configuration_value "$AZURE_CONFIGURATION" AZURE_CUSTOM_DOMAIN)"
  if [[ -n "$domain" && "$AZURE_CUSTOM_DOMAIN_READY" == true ]]; then
    test_azure_deployment "$AZURE_CONFIGURATION_ENVIRONMENT_NAME" "https://$domain"
  fi
}

read_azure_domain_continuation() {
  local selection=''
  printf '\n'
  printf '%s\n' 'Press 4 to retry domain setup, or any other key to return to the menu without clearing the output.'
  if [[ -t 0 ]]; then
    IFS= read -r -s -n 1 selection || return 1
  else
    IFS= read -r selection || return 1
    selection="${selection%$'\r'}"
  fi
  [[ "$selection" == '4' ]]
}

invoke_azure_up() {
  local environmentName serviceUri
  invoke_azure_check
  environmentName="$AZURE_CONFIGURATION_ENVIRONMENT_NAME"
  invoke_container_build
  save_azure_domain_bindings
  run_tool 'Previewing Azure infrastructure changes' azd provision --preview --environment "$environmentName"
  if should_process "Azure environment $environmentName" 'Provision billable resources and deploy MockAPI'; then
    run_tool 'Provisioning and deploying MockAPI to Azure' azd up --environment "$environmentName"
    complete_azure_deployment
    serviceUri="$AZURE_SERVICE_URI"
    write_azure_service_summary "$serviceUri"
  fi
}

invoke_azure_deploy() {
  local environmentName serviceUri
  invoke_azure_check
  environmentName="$AZURE_CONFIGURATION_ENVIRONMENT_NAME"
  if should_process "Azure environment $environmentName" 'Build and deploy the MockAPI container image'; then
    run_tool 'Deploying MockAPI to Azure' azd deploy --environment "$environmentName"
    complete_azure_deployment
    serviceUri="$AZURE_SERVICE_URI"
    write_azure_service_summary "$serviceUri"
  fi
}

invoke_azure_push() {
  local environmentName serviceUri
  invoke_azure_check
  environmentName="$AZURE_CONFIGURATION_ENVIRONMENT_NAME"
  if should_process "Azure environment $environmentName" 'Build and push the MockAPI container image'; then
    run_tool 'Building and pushing MockAPI to Azure Container Registry' azd publish mockapi --environment "$environmentName" --no-prompt
    serviceUri="$(get_azure_service_uri "$environmentName")"
    write_azure_service_summary "$serviceUri"
  fi
}

invoke_azure_import() {
  invoke_azure_check
  local location environmentName
  location="$AZURE_CONFIGURATION_LOCATION"
  environmentName="$AZURE_CONFIGURATION_ENVIRONMENT_NAME"

  local resourceGroup="rg-${environmentName}-${location}"
  local appVersion
  appVersion="$(get_application_version)"

  local registryEndpoint
  registryEndpoint="$(get_azd_environment_value "$environmentName" AZURE_CONTAINER_REGISTRY_ENDPOINT)"
  local registryName="${registryEndpoint%%.*}"

  local sourceImage="${PUBLIC_IMAGE_REPOSITORY}:v${appVersion}"
  local targetImage="mockapi:v${appVersion}"

  local containerAppName
  containerAppName="$(az resource list \
    --resource-group "$resourceGroup" \
    --tag 'azd-service-name=mockapi' \
    --query "[?type=='Microsoft.App/containerApps'] | [0].name" \
    --output tsv 2>/dev/null || true)"
  containerAppName="$(trim "$containerAppName")"
  [[ -n "$containerAppName" ]] || die "No Container App was found in resource group '$resourceGroup'."

  if should_process "Azure environment $environmentName" "Import $sourceImage into ACR and deploy it"; then
    run_tool "Importing $sourceImage into Azure Container Registry" az acr import \
      --name "$registryName" --source "$sourceImage" --image "$targetImage"
    run_tool "Deploying ${registryEndpoint}/${targetImage} to $containerAppName" az containerapp update \
      --name "$containerAppName" --resource-group "$resourceGroup" \
      --image "${registryEndpoint}/${targetImage}"
    local serviceUri
    complete_azure_deployment
    serviceUri="$AZURE_SERVICE_URI"
    write_azure_service_summary "$serviceUri"
  fi
}

invoke_azure_down() {
  local environmentName resourceGroup
  invoke_azure_check
  environmentName="$AZURE_CONFIGURATION_ENVIRONMENT_NAME"
  resourceGroup="rg-${environmentName}-${AZURE_CONFIGURATION_LOCATION}"
  if should_process "Azure resource group $resourceGroup" 'Delete Azure resources'; then
    run_interactive_tool 'Requesting asynchronous deletion of MockAPI Azure resources' az group delete \
      --name "$resourceGroup" --subscription "$AZURE_CONFIGURATION_SUBSCRIPTION" --no-wait
  fi
}

# ---------------------------------------------------------------------------
# Help.
# ---------------------------------------------------------------------------
show_help() {
  cat <<EOF

MockAPI Developer CLI (bash)

Usage:
  ./start.sh [--action <name>] [options]

Interactive menu (no action required):
  Run locally, Verify, Azure, Containers, Setup, Help, Quit.
  Choose a group to see its actions; use b to go back.
  Existing action shortcuts (such as p1, a4, c3) work from any menu.


1) Pathways

   pathway-azure-initial  Test, build, and deploy the initial Azure environment.
   pathway-azure-update   Test, build, and update an existing Azure deployment.


2) Setup

   check              Verify the pinned .NET SDK, pnpm, and selected container engine.
   setup              Check prerequisites and restore development dependencies.
   dependencies-update Check for and install pnpm dependency updates that completed the eight-day cooldown.
   pnpm-update         Update the project pnpm pin to the latest release that completed the cooldown.
   restore            Restore NuGet packages.


3) Local Development

   run                Run locally and open the dashboard without the tutorial.
   run-tutorial       Run locally and open the dashboard with the tutorial.


4) Verification

   format             Format supported repository files and managed code.
   lint               Check formatting, Markdown, and managed code style.
   build              Build with warnings treated as errors.
   test               Run all tests.
   coverage           Run tests and write Cobertura output under artifacts/coverage.
   publish            Cross-publish trimmed CoreCLR artifacts for managed-code checks, not release images.
   validate           Restore, build, test, collect coverage, and publish.
   all                Run managed validation and build the native container image.


5) Container Operations (WSLC or Docker)

   container-build    Build a native AOT image and move the ${IMAGE_NAME} alias to its unique build tag.
                      Removing dashboard assets saves very little image space; set
                      MockApi__EnableDashboard=false to disable the dashboard at runtime.
   container-run      Create or start ${CONTAINER_NAME}; full-mode containers prompt for optional dashboard credentials.
   container-test     Send an HTTP smoke test to the running container.
   container-showcase Load the built-in examples when needed, then verify rate-limit response and statistics behavior.
   container-logs     Show container logs.
   container-status   Inspect the container.
   container-stop     Stop the container with a one-second graceful shutdown window.
   container-remove   Remove the stopped container; the data volume is retained.


6) Azure (Container Apps via azd)

   azure-setup        Verify Azure tooling; use --install-missing for installation guidance.
   azure-check        Validate .env, azd authentication/context, azure.yaml, and Bicep.
   azure-up           Build locally, preview infrastructure, then provision and deploy.
   azure-import       Import the public versioned Docker Hub image into ACR and deploy it.
   azure-push         Build and push the application image without updating Container Apps.
   azure-deploy       Build and deploy the application to existing Azure resources.
   azure-down         Confirm resource-group deletion, submit it, and return without waiting.

   Set AZURE_CUSTOM_DOMAIN in .env for an Azure-managed certificate.
   Set AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=CNAME (default) or HTTP for an apex domain.
   Deployment prints required DNS records; configure DNS and rerun azure-deploy if needed.


7) Misc

   help               Show this help.

Options
  --install-missing      Permit setup/check actions to install missing supported tooling.
  --skip-container-check  Skip container-engine inspection for isolated CI execution only.
  --container-engine     Local container engine: wslc (default) or docker.
  --configuration        Build configuration; default: Release.
  --image-name           Container image; default: mockapi:dev.
  --container-name        Container name; default: mockapi-dev.
  --volume-name          Persistent /data volume; default: mockapi-data.
  --nuget-source         NuGet HTTPS source for container restore; default: first enabled host source.
  --port                 Host port; default: 8080.
  --environment-file     Local Azure settings file; default: .env.
  --what-if              Preview Azure mutations without executing them.
  --confirm              Prompt before executing an Azure mutation.
EOF
}

show_submenu_help() {
  case "$1" in
    'Run locally')
      cat <<EOF
MockAPI Developer CLI

 Run locally help

  [1] Run without tutorial
      Launch with local .NET and open the dashboard without the first-use tour.
  [2] Run with tutorial
      Launch with local .NET and open the first-use tour, even if previously dismissed.

  The launch choice does not reset saved dashboard preferences.
  Use run or run-tutorial for the equivalent command-line actions.
EOF
      ;;
    Verify)
      cat <<EOF
MockAPI Developer CLI

 Verify help

  [1] Validate managed code
       Restore, scan dependencies, lint, build, test, collect coverage, and publish.
  [2] Run unit tests
       Run the repository test suites.
EOF
      ;;
    Azure)
      cat <<EOF
MockAPI Developer CLI

 Azure help

  Set AZURE_CUSTOM_DOMAIN in .env for an Azure-managed certificate.
  Set AZURE_CUSTOM_DOMAIN_VALIDATION_METHOD=CNAME (default) or HTTP for an apex domain.
  Deployment prints required DNS records; configure DNS and rerun azure-deploy if needed.

  [1] Test, build, and deploy initial Azure environment
       Run tests and build before provisioning and deploying.
  [2] Test, build, and update existing Azure deployment
       Run tests and build before updating an existing deployment.
  [3] Set up Azure Tooling
       Verify Azure tooling and optionally install or update it.
  [4] Validate deployment configuration
       Validate local settings, authentication, azd configuration, and Bicep.
  [5] Provision and deploy
       Build locally, preview infrastructure, then provision and deploy.
  [6] Build, push, and deploy image
       Build and deploy the application to existing Azure resources.
  [7] Build and push image only
       Build and push the application image without updating Container Apps.
  [8] Import Docker Hub image and deploy
       Import the public versioned image into ACR and deploy it.
  [9] Delete Azure resources
       Delete the azd environment resources with confirmation.
EOF
      ;;
    Containers)
      cat <<EOF
MockAPI Developer CLI

 Containers help

  [1] Use WSLC
       Use WSLC for local container actions.
  [2] Use Docker
       Use Docker for local container actions.
  [3] Build Dockerfile (${CONTAINER_ENGINE})
       Build a uniquely tagged image and update the local image alias.
  [4] Start or restart native container (${CONTAINER_ENGINE})
       Create or start the development container.
  [5] Test running container
       Send an HTTP smoke test to the running container.
  [6] Load and showcase built-in example
       Load examples and verify rate limiting and statistics.
  [7] Show container logs
       Show recent container log lines.
  [8] Show container status
       Inspect the container.
  [9] Stop container
       Stop the container with a one-second graceful shutdown window.
  [10] Remove container
       Remove the stopped container while retaining its data volume.
EOF
      ;;
    Setup)
      cat <<EOF
MockAPI Developer CLI

 Setup help

  [1] Setup local dependencies
       Check prerequisites and restore development dependencies.
  [2] Update dependencies and pnpm meeting the cooldown
       Update eligible dependencies first, then update the pnpm pin.
EOF
      ;;
    *) die "Unknown submenu help section '$1'." ;;
  esac
}

# ---------------------------------------------------------------------------
# Interactive menu (mirrors Show-Menu in start.ps1).
# ---------------------------------------------------------------------------
show_menu() {
  MENU_SELECTION=""
  local currentEngine="$CONTAINER_ENGINE"
  local currentMenu="${MENU_CONTEXT:-Home}"
  while true; do
    reset_console_colors
    printf '\n'
    write_color cyan 'MockAPI Developer CLI'
    write_color cyan '====================='

    printf '\n'
    write_color yellow "$currentMenu"
    printf '\n'
    case "$currentMenu" in
      Home)
        printf '   %-5s  %s\n' '[l]' 'Run locally'
        printf '   %-5s  %s\n' '[v]' 'Verify'
        printf '   %-5s  %s\n' '[a]' 'Azure'
        printf '   %-5s  %s\n' '[c]' 'Containers'
        printf '   %-5s  %s\n' '[s]' 'Setup'
        printf '\n'
        printf '   %-5s  %s\n' '[h]' 'Help'
        printf '   %-5s  %s\n' '[q]' 'Quit'
        ;;
      'Run locally')
        printf '   %-5s  %s\n' '[1]' 'Run without tutorial'
        printf '   %-5s  %s\n' '[2]' 'Run with tutorial'
        ;;
      Verify)
        printf '   %-5s  %s\n' '[1]' 'Validate managed code'
        printf '   %-5s  %s\n' '[2]' 'Run unit tests'
        ;;
      Azure)
        printf '   %-5s  %s\n' '[1]' 'Test, build, and deploy initial Azure environment'
        printf '   %-5s  %s\n' '[2]' 'Test, build, and update existing Azure deployment'
        printf '   %-5s  %s\n' '[3]' 'Set up Azure Tooling'
        printf '   %-5s  %s\n' '[4]' 'Validate deployment configuration'
        printf '   %-5s  %s\n' '[5]' 'Provision and deploy'
        printf '   %-5s  %s\n' '[6]' 'Build, push, and deploy image'
        printf '   %-5s  %s\n' '[7]' 'Build and push image only'
        printf '   %-5s  %s\n' '[8]' 'Import Docker Hub image and deploy'
        printf '   %-5s  %s\n' '[9]' 'Delete Azure resources'
        ;;
      Containers)
        printf '   %-5s  %s\n' '[1]' 'Use WSLC'
        printf '   %-5s  %s\n' '[2]' 'Use Docker'
        printf '   %-5s  %s\n' '[3]' "Build Dockerfile ($currentEngine)"
        printf '   %-5s  %s\n' '[4]' "Start or restart native container ($currentEngine)"
        printf '   %-5s  %s\n' '[5]' 'Test running container'
        printf '   %-5s  %s\n' '[6]' 'Load and showcase built-in example'
        printf '   %-5s  %s\n' '[7]' 'Show container logs'
        printf '   %-5s  %s\n' '[8]' 'Show container status'
        printf '   %-5s  %s\n' '[9]' 'Stop container'
        printf '   %-5s  %s\n' '[10]' 'Remove container'
        ;;
      Setup)
        printf '   %-5s  %s\n' '[1]' 'Setup local dependencies'
        printf '   %-5s  %s\n' '[2]' 'Update dependencies and pnpm meeting the cooldown'
        ;;
    esac
    if [[ "$currentMenu" != 'Home' ]]; then
      printf '\n'
      printf '   %-5s  %s\n' '[b]' 'Back'
      printf '   %-5s  %s\n' '[h]' 'Help'
      printf '   %-5s  %s\n' '[q]' 'Quit'
    fi

    printf '\n'
    printf '%s\n' 'Action shortcuts work from any menu.'
    printf '\n'
    local selection
    read -r -p 'Select an action: ' selection || true
    printf '\n'
    selection="$(printf '%s' "$selection" | tr '[:upper:]' '[:lower:]')"
    MENU_CONTEXT="$currentMenu"

    case "$currentMenu:$selection" in
      'Run locally:1') MENU_SELECTION='run'; return 0 ;;
      'Run locally:2') MENU_SELECTION='run-tutorial'; return 0 ;;
      'Verify:1') MENU_SELECTION='validate'; return 0 ;;
      'Verify:2') MENU_SELECTION='test'; return 0 ;;
      'Azure:1') MENU_SELECTION='pathway-azure-initial'; return 0 ;;
      'Azure:2') MENU_SELECTION='pathway-azure-update'; return 0 ;;
      'Azure:3') MENU_SELECTION='azure-setup'; return 0 ;;
      'Azure:4') MENU_SELECTION='azure-check'; return 0 ;;
      'Azure:5') MENU_SELECTION='azure-up'; return 0 ;;
      'Azure:6') MENU_SELECTION='azure-deploy'; return 0 ;;
      'Azure:7') MENU_SELECTION='azure-push'; return 0 ;;
      'Azure:8') MENU_SELECTION='azure-import'; return 0 ;;
      'Azure:9') MENU_SELECTION='azure-down'; return 0 ;;
      'Containers:1') MENU_SELECTION='container-engine-wslc'; return 0 ;;
      'Containers:2') MENU_SELECTION='container-engine-docker'; return 0 ;;
      'Containers:3') MENU_SELECTION='container-build'; return 0 ;;
      'Containers:4') MENU_SELECTION='container-run'; return 0 ;;
      'Containers:5') MENU_SELECTION='container-test'; return 0 ;;
      'Containers:6') MENU_SELECTION='container-showcase'; return 0 ;;
      'Containers:7') MENU_SELECTION='container-logs'; return 0 ;;
      'Containers:8') MENU_SELECTION='container-status'; return 0 ;;
      'Containers:9') MENU_SELECTION='container-stop'; return 0 ;;
      'Containers:10') MENU_SELECTION='container-remove'; return 0 ;;
      'Setup:1') MENU_SELECTION='setup'; return 0 ;;
      'Setup:2') MENU_SELECTION='dependency-tooling-update'; return 0 ;;
    esac

    case "$selection" in
      l) currentMenu='Run locally' ;;
      v) currentMenu='Verify' ;;
      a) currentMenu='Azure' ;;
      c) currentMenu='Containers' ;;
      s) currentMenu='Setup' ;;
      b)
        if [[ "$currentMenu" == 'Home' ]]; then
          write_color red "Unknown menu selection '$selection'."
        else
          currentMenu='Home'
        fi
        ;;
      p1) MENU_SELECTION='pathway-azure-initial'; return 0 ;;
      p2) MENU_SELECTION='pathway-azure-update'; return 0 ;;
      s1) MENU_SELECTION='setup'; return 0 ;;
      s2) MENU_SELECTION='dependency-tooling-update'; return 0 ;;
      s3) MENU_SELECTION='pnpm-update'; return 0 ;;
      l1) MENU_SELECTION='run'; return 0 ;;
      v1) MENU_SELECTION='validate'; return 0 ;;
      v2) MENU_SELECTION='test'; return 0 ;;
      c1) MENU_SELECTION='container-engine-wslc'; return 0 ;;
      c2) MENU_SELECTION='container-engine-docker'; return 0 ;;
      c3) MENU_SELECTION='container-build'; return 0 ;;
      c4) MENU_SELECTION='container-run'; return 0 ;;
      c5) MENU_SELECTION='container-test'; return 0 ;;
      c6) MENU_SELECTION='container-showcase'; return 0 ;;
      c7) MENU_SELECTION='container-logs'; return 0 ;;
      c8) MENU_SELECTION='container-status'; return 0 ;;
      c9) MENU_SELECTION='container-stop'; return 0 ;;
      c10) MENU_SELECTION='container-remove'; return 0 ;;
      a1) MENU_SELECTION='azure-setup'; return 0 ;;
      a2) MENU_SELECTION='azure-check'; return 0 ;;
      a3) MENU_SELECTION='azure-up'; return 0 ;;
      a4) MENU_SELECTION='azure-deploy'; return 0 ;;
      a5) MENU_SELECTION='azure-push'; return 0 ;;
      a6) MENU_SELECTION='azure-import'; return 0 ;;
      a7) MENU_SELECTION='azure-down'; return 0 ;;
      h)
        if [[ "$currentMenu" == 'Home' ]]; then
          MENU_SELECTION='help'
        else
          MENU_SELECTION="help-$(printf '%s' "$currentMenu" | tr '[:upper:]' '[:lower:]' | tr ' ' '-')"
        fi
        return 0
        ;;
      q) MENU_SELECTION='quit'; return 0 ;;
      *) write_color red "Unknown menu selection '$selection'." ;;
    esac
  done
}

wait_for_menu_return() {
  if [[ "$DOMAIN_CONTINUATION_PROMPTED" == true ]]; then
    DOMAIN_CONTINUATION_PROMPTED=false
    return
  fi
  printf '\n\n'
  write_color dark '===================================='
  write_color cyan 'Press any key to return to the menu.'
  write_color dark '===================================='

  if [[ -t 0 ]]; then
    IFS= read -r -s -n 1 _ || true
  else
    IFS= read -r _ || true
  fi
}

# ---------------------------------------------------------------------------
# Action dispatch (mirrors Invoke-Action in start.ps1).
# ---------------------------------------------------------------------------
invoke_action() {
  local selectedAction="$1"
  case "$selectedAction" in
    help) show_help ;;
    help-run-locally) show_submenu_help 'Run locally' ;;
    help-verify) show_submenu_help Verify ;;
    help-azure) show_submenu_help Azure ;;
    help-containers) show_submenu_help Containers ;;
    help-setup) show_submenu_help Setup ;;
    check) test_prerequisites ;;
    setup) invoke_setup ;;
    dependencies-update) invoke_dependency_updates ;;
    pnpm-update) invoke_pnpm_update ;;
    dependency-tooling-update) invoke_dependency_tooling_updates ;;
    restore) invoke_restore ;;
    format) invoke_format ;;
    lint) invoke_lint ;;
    build) invoke_build ;;
    run) invoke_run ;;
    run-tutorial) invoke_run show ;;
    test) invoke_tests ;;
    coverage) invoke_coverage ;;
    publish) invoke_publish ;;
    validate) invoke_validation ;;
    container-engine-wslc) CONTAINER_ENGINE='wslc'; write_field 'Container engine' 'WSLC' green ;;
    container-engine-docker) CONTAINER_ENGINE='docker'; write_field 'Container engine' 'DOCKER' green ;;
    container-build) invoke_container_build ;;
    container-run) invoke_container_run ;;
    container-test) invoke_container_test ;;
    container-showcase) invoke_container_showcase ;;
    container-logs) invoke_container_logs ;;
    container-status) invoke_container_status ;;
    container-stop) invoke_container_stop ;;
    container-remove) invoke_container_remove ;;
    azure-setup) invoke_azure_setup ;;
    azure-check) invoke_azure_check ;;
    azure-up) invoke_azure_up ;;
    azure-import) invoke_azure_import ;;
    azure-push) invoke_azure_push ;;
    azure-deploy) invoke_azure_deploy ;;
    azure-down) invoke_azure_down ;;
    pathway-azure-initial) invoke_initial_azure_pathway ;;
    pathway-azure-update) invoke_update_azure_pathway ;;
    all) invoke_all ;;
    *) die "Unknown action '$selectedAction'." ;;
  esac
}

# ---------------------------------------------------------------------------
# Argument parsing.
# ---------------------------------------------------------------------------
VALID_ACTIONS="menu help check setup dependencies-update pnpm-update restore format lint build run run-tutorial test coverage publish validate container-engine-wslc container-engine-docker container-build container-run container-test container-showcase container-logs container-status container-stop container-remove azure-setup azure-check azure-up azure-import azure-push azure-deploy azure-down pathway-azure-initial pathway-azure-update all"

is_valid_action() {
  local candidate="$1" action
  for action in $VALID_ACTIONS; do
    [[ "$action" == "$candidate" ]] && return 0
  done
  return 1
}

parse_arguments() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --action)
        [[ $# -ge 2 ]] || die '--action requires a value.'
        ACTION="$2"
        shift 2
        ;;
      --action=*)
        ACTION="${1#*=}"
        shift
        ;;
      --install-missing)
        INSTALL_MISSING="true"
        shift
        ;;
      --skip-container-check)
        SKIP_CONTAINER_CHECK="true"
        shift
        ;;
      --what-if)
        WHAT_IF="true"
        shift
        ;;
      --confirm)
        CONFIRM="true"
        shift
        ;;
      --container-engine)
        [[ $# -ge 2 ]] || die '--container-engine requires a value.'
        CONTAINER_ENGINE="$2"
        shift 2
        ;;
      --container-engine=*)
        CONTAINER_ENGINE="${1#*=}"
        shift
        ;;
      --configuration)
        [[ $# -ge 2 ]] || die '--configuration requires a value.'
        CONFIGURATION="$2"
        shift 2
        ;;
      --configuration=*)
        CONFIGURATION="${1#*=}"
        shift
        ;;
      --image-name)
        [[ $# -ge 2 ]] || die '--image-name requires a value.'
        IMAGE_NAME="$2"
        shift 2
        ;;
      --image-name=*)
        IMAGE_NAME="${1#*=}"
        shift
        ;;
      --container-name)
        [[ $# -ge 2 ]] || die '--container-name requires a value.'
        CONTAINER_NAME="$2"
        shift 2
        ;;
      --container-name=*)
        CONTAINER_NAME="${1#*=}"
        shift
        ;;
      --volume-name)
        [[ $# -ge 2 ]] || die '--volume-name requires a value.'
        VOLUME_NAME="$2"
        shift 2
        ;;
      --volume-name=*)
        VOLUME_NAME="${1#*=}"
        shift
        ;;
      --nuget-source)
        [[ $# -ge 2 ]] || die '--nuget-source requires a value.'
        NUGET_SOURCE="$2"
        shift 2
        ;;
      --nuget-source=*)
        NUGET_SOURCE="${1#*=}"
        shift
        ;;
      --port)
        [[ $# -ge 2 ]] || die '--port requires a value.'
        PORT="$2"
        shift 2
        ;;
      --port=*)
        PORT="${1#*=}"
        shift
        ;;
      --environment-file)
        [[ $# -ge 2 ]] || die '--environment-file requires a value.'
        ENVIRONMENT_FILE="$2"
        shift 2
        ;;
      --environment-file=*)
        ENVIRONMENT_FILE="${1#*=}"
        shift
        ;;
      -h|--help)
        ACTION="help"
        shift
        ;;
      *)
        die "Unknown argument '$1'. Run './start.sh --action help' for usage."
        ;;
    esac
  done
}

validate_arguments() {
  is_valid_action "$ACTION" || die "Unknown action '$ACTION'. Run './start.sh --action help' for the list of actions."
  case "$CONTAINER_ENGINE" in
    wslc|docker) ;;
    *) die "Unknown container engine '$CONTAINER_ENGINE'. Use wslc or docker." ;;
  esac
  [[ -n "$CONFIGURATION" ]] || die '--configuration cannot be empty.'
  if ! [[ "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1 || PORT > 65535 )); then
    die "--port must be an integer between 1 and 65535 (received '$PORT')."
  fi
}

# ---------------------------------------------------------------------------
# Main.
# ---------------------------------------------------------------------------
main() {
  parse_arguments "$@"
  validate_arguments

  require_command python3 || die 'python3 is required by the MockAPI developer CLI. Install Python 3 and retry.'
  require_command curl || die 'curl is required by the MockAPI developer CLI. Install curl and retry.'

  cd "$REPOSITORY_ROOT"

  if [[ "$ACTION" != "menu" ]]; then
    invoke_action "$ACTION"
    return 0
  fi

  MENU_CONTEXT='Home'
  clear
  while true; do
    show_menu
    local selection="$MENU_SELECTION"
    if [[ "$selection" == "quit" ]]; then
      break
    fi
    if [[ "$selection" == "container-engine-wslc" ]]; then
      CONTAINER_ENGINE="wslc"
      write_field 'Container engine' 'WSLC' green
      wait_for_menu_return
      continue
    fi
    if [[ "$selection" == "container-engine-docker" ]]; then
      CONTAINER_ENGINE="docker"
      write_field 'Container engine' 'DOCKER' green
      wait_for_menu_return
      continue
    fi
    # Keep the return handler in the action subshell so it sees domain-continuation state.
    (
      trap '
        actionExitCode=$?
        if (( actionExitCode != 0 )); then
          write_color red "Action failed: $selection"
        fi
        wait_for_menu_return
      ' EXIT
      invoke_action "$selection"
    ) || true
  done
}

main "$@"
