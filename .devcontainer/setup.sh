#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
export NVM_DIR="${NVM_DIR:-/usr/local/share/nvm}"
# The digest-pinned development image supplies nvm; use an age-verified exact Node release.
source "$NVM_DIR/nvm.sh" --no-use
nvm install "$MOCKAPI_NODE_VERSION"
nvm alias default "$MOCKAPI_NODE_VERSION"
nvm use "$MOCKAPI_NODE_VERSION"
package_manager=$(node -p "require('./package.json').packageManager")
npm install --global "$package_manager"

# Install the exact stable SDK required by global.json into the pinned development image.
dotnet_install_script="$(mktemp)"
trap 'rm -f "$dotnet_install_script"' EXIT
curl --fail --location --silent --show-error \
  https://dot.net/v1/dotnet-install.sh --output "$dotnet_install_script"
bash "$dotnet_install_script" --version 11.0.100 --install-dir "$HOME/.dotnet" --no-path
export PATH="$HOME/.dotnet:$PATH"
resolved_dotnet_version=$(dotnet --version)
[[ "$resolved_dotnet_version" == 11.* ]] || {
  printf 'Expected the .NET 11 SDK, but resolved %s.\n' "$resolved_dotnet_version" >&2
  exit 1
}

mkdir -p artifacts/local-data
# Do not seed or overwrite a developer's saved configuration on rebuild.
pnpm install --frozen-lockfile
./start.sh --action restore
printf '\nReady. Run ./start.sh --action run-tutorial, then open private forwarded port 5080.\n'
