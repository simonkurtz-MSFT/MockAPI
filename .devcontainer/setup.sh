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

mkdir -p artifacts/local-data
# Do not seed or overwrite a developer's saved configuration on rebuild.
pnpm install --frozen-lockfile
./start.sh --action restore
printf '\nReady. Run ./start.sh --action run-tutorial, then open private forwarded port 5080.\n'
